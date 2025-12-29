use crate::models::{AuthMethod, FileEntry, ProxyConfig, ProxyType, ServerConfig, TerminalSize};
use crate::sftp::{self, SftpConnection};
use crate::ssh::{self, SshSession};
use crate::storage;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use tauri::{AppHandle, Emitter};
use tokio::fs;

// ============ Server Management Commands ============

#[derive(Debug, Serialize)]
pub struct ServerInfo {
    pub id: String,
    pub name: String,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub auth_type: String,
    pub has_proxy: bool,
    pub has_jump_host: bool,
    pub jump_host: Option<String>,
    pub notes: Option<String>,
}

impl From<&ServerConfig> for ServerInfo {
    fn from(config: &ServerConfig) -> Self {
        Self {
            id: config.id.clone(),
            name: config.name.clone(),
            host: config.host.clone(),
            port: config.port,
            username: config.username.clone(),
            auth_type: match &config.auth {
                AuthMethod::Password(_) => "password".to_string(),
                AuthMethod::PrivateKey { .. } => "key".to_string(),
            },
            has_proxy: config.proxy.is_some(),
            has_jump_host: config.jump_host.is_some(),
            jump_host: config.jump_host.clone(),
            notes: config.notes.clone(),
        }
    }
}

#[tauri::command]
pub fn get_servers() -> Vec<ServerInfo> {
    storage::get_all_servers()
        .iter()
        .map(ServerInfo::from)
        .collect()
}

#[tauri::command]
pub fn get_server(id: String) -> Option<ServerConfig> {
    storage::get_server(&id)
}

#[derive(Debug, Deserialize)]
pub struct SaveServerRequest {
    pub id: Option<String>,
    pub name: String,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub auth_type: String,
    pub password: Option<String>,
    pub private_key: Option<String>,
    pub passphrase: Option<String>,
    pub proxy_enabled: bool,
    pub proxy_type: Option<String>,
    pub proxy_host: Option<String>,
    pub proxy_port: Option<u16>,
    pub proxy_username: Option<String>,
    pub proxy_password: Option<String>,
    pub jump_host: Option<String>,
    pub notes: Option<String>,
}

#[tauri::command]
pub fn save_server(request: SaveServerRequest) -> Result<ServerInfo, String> {
    let auth = if request.auth_type == "key" {
        AuthMethod::PrivateKey {
            key: request.private_key.ok_or("Private key is required")?,
            passphrase: request.passphrase.filter(|p| !p.is_empty()),
        }
    } else {
        AuthMethod::Password(request.password.ok_or("Password is required")?)
    };

    let proxy = if request.proxy_enabled {
        Some(ProxyConfig {
            proxy_type: match request.proxy_type.as_deref() {
                Some("socks5") => ProxyType::Socks5,
                _ => ProxyType::Http,
            },
            host: request.proxy_host.ok_or("Proxy host is required")?,
            port: request.proxy_port.ok_or("Proxy port is required")?,
            username: request.proxy_username,
            password: request.proxy_password,
        })
    } else {
        None
    };

    // Filter empty jump_host
    let jump_host = request.jump_host.filter(|h| !h.is_empty());

    let server = if let Some(id) = request.id {
        let mut existing = storage::get_server(&id).ok_or("Server not found")?;
        existing.name = request.name;
        existing.host = request.host;
        existing.port = request.port;
        existing.username = request.username;
        existing.auth = auth;
        existing.proxy = proxy;
        existing.jump_host = jump_host;
        existing.notes = request.notes;
        existing
    } else {
        let mut server = ServerConfig::new(request.name, request.host, request.port, request.username, auth);
        server.proxy = proxy;
        server.jump_host = jump_host;
        server.notes = request.notes;
        server
    };

    let saved = storage::save_server(server).map_err(|e| e.to_string())?;
    Ok(ServerInfo::from(&saved))
}

#[tauri::command]
pub fn delete_server(id: String) -> Result<(), String> {
    storage::delete_server(&id).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn export_servers(password: String) -> Result<String, String> {
    storage::export_servers(&password).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn import_servers(encrypted_data: String, password: String) -> Result<usize, String> {
    storage::import_servers(&encrypted_data, &password).map_err(|e| e.to_string())
}

// ============ SSH Commands ============

#[tauri::command]
pub async fn ssh_connect(
    app: AppHandle,
    server_id: String,
    cols: u32,
    rows: u32,
) -> Result<String, String> {
    let server = storage::get_server(&server_id).ok_or("Server not found")?;

    let session = SshSession::connect(&server)
        .await
        .map_err(|e| e.to_string())?;

    let session_id = session.session_id().to_string();

    session
        .open_shell(TerminalSize { cols, rows })
        .await
        .map_err(|e| e.to_string())?;

    // Spawn output handler
    if let Some(mut rx) = session.take_output_receiver().await {
        let app_handle = app.clone();
        let sid = session_id.clone();
        tokio::spawn(async move {
            while let Some(data) = rx.recv().await {
                let _ = app_handle.emit(&format!("ssh-data-{}", sid), data);
            }
        });
    }

    Ok(session_id)
}

#[tauri::command]
pub async fn ssh_write(session_id: String, data: Vec<u8>) -> Result<(), String> {
    let session = ssh::get_session(&session_id).await.ok_or("Session not found")?;
    session.write(&data).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn ssh_resize(session_id: String, cols: u32, rows: u32) -> Result<(), String> {
    let session = ssh::get_session(&session_id).await.ok_or("Session not found")?;
    session
        .resize(TerminalSize { cols, rows })
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn ssh_disconnect(session_id: String) -> Result<(), String> {
    if let Some(session) = ssh::get_session(&session_id).await {
        session.close().await.map_err(|e| e.to_string())?;
    }
    ssh::remove_session(&session_id).await;
    Ok(())
}

// ============ SFTP Commands ============

#[tauri::command]
pub async fn sftp_connect(server_id: String) -> Result<String, String> {
    let server = storage::get_server(&server_id).ok_or("Server not found")?;

    let session = SftpConnection::connect(&server)
        .await
        .map_err(|e| e.to_string())?;

    Ok(session.session_id().to_string())
}

#[tauri::command]
pub async fn sftp_list_dir(session_id: String, path: String) -> Result<Vec<FileEntry>, String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session.list_dir(&path).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_read_file(session_id: String, path: String) -> Result<Vec<u8>, String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session.read_file(&path).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_write_file(
    session_id: String,
    path: String,
    contents: Vec<u8>,
) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session
        .write_file(&path, &contents)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_delete(session_id: String, path: String, is_dir: bool) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session
        .delete(&path, is_dir)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_rename(
    session_id: String,
    old_path: String,
    new_path: String,
) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session
        .rename(&old_path, &new_path)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_create_dir(session_id: String, path: String) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id).await.ok_or("SFTP session not found")?;
    session.create_dir(&path).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_disconnect(session_id: String) -> Result<(), String> {
    if let Some(session) = sftp::get_sftp_session(&session_id).await {
        session.close().await.map_err(|e| e.to_string())?;
    }
    sftp::remove_sftp_session(&session_id).await;
    Ok(())
}

#[tauri::command]
pub async fn sftp_create_file(session_id: String, path: String) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id)
        .await
        .ok_or("SFTP session not found")?;
    session
        .write_file(&path, &[])
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn sftp_download(
    session_id: String,
    remote_path: String,
    local_path: String,
) -> Result<(), String> {
    let session = sftp::get_sftp_session(&session_id)
        .await
        .ok_or("SFTP session not found")?;

    let contents = session
        .read_file(&remote_path)
        .await
        .map_err(|e| e.to_string())?;

    let path = PathBuf::from(&local_path);
    fs::write(&path, &contents)
        .await
        .map_err(|e| format!("Failed to write local file: {}", e))?;

    Ok(())
}

#[tauri::command]
pub async fn sftp_upload(
    session_id: String,
    local_path: String,
    remote_path: String,
) -> Result<(), String> {
    let path = PathBuf::from(&local_path);
    let contents = fs::read(&path)
        .await
        .map_err(|e| format!("Failed to read local file: {}", e))?;

    let session = sftp::get_sftp_session(&session_id)
        .await
        .ok_or("SFTP session not found")?;

    session
        .write_file(&remote_path, &contents)
        .await
        .map_err(|e| e.to_string())
}
