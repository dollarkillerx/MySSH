use crate::models::{AuthMethod, FileEntry, ProxyConfig, ProxyType, ServerConfig};
use anyhow::{Context, Result};
use async_trait::async_trait;
use once_cell::sync::Lazy;
use russh::keys::*;
use russh::*;
use russh_sftp::client::SftpSession;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::RwLock;
use tokio_socks::tcp::Socks5Stream;

static SFTP_SESSIONS: Lazy<RwLock<HashMap<String, Arc<SftpConnection>>>> =
    Lazy::new(|| RwLock::new(HashMap::new()));

pub struct SftpConnection {
    session_id: String,
    sftp: SftpSession,
    _handle: client::Handle<SftpHandler>,
}

impl SftpConnection {
    pub async fn connect(server: &ServerConfig) -> Result<Arc<Self>> {
        let session_id = uuid::Uuid::new_v4().to_string();

        // Create TCP connection
        let stream = Self::create_connection(server).await?;

        // SSH config
        let config = client::Config::default();
        let config = Arc::new(config);

        let handler = SftpHandler;

        let handle = client::connect_stream(config, stream, handler)
            .await
            .context("Failed to establish SSH connection")?;

        // Authenticate
        let mut handle = handle;
        Self::authenticate(&mut handle, server).await?;

        // Open SFTP channel
        let channel = handle
            .channel_open_session()
            .await
            .context("Failed to open channel")?;

        channel
            .request_subsystem(false, "sftp")
            .await
            .context("Failed to request SFTP subsystem")?;

        let sftp = SftpSession::new(channel.into_stream()).await?;

        let connection = Arc::new(Self {
            session_id: session_id.clone(),
            sftp,
            _handle: handle,
        });

        SFTP_SESSIONS.write().await.insert(session_id, connection.clone());

        Ok(connection)
    }

    async fn create_connection(server: &ServerConfig) -> Result<TcpStream> {
        let target_addr = format!("{}:{}", server.host, server.port);

        match &server.proxy {
            Some(proxy) => Self::connect_via_proxy(proxy, &target_addr).await,
            None => TcpStream::connect(&target_addr)
                .await
                .context("Failed to connect to server"),
        }
    }

    async fn connect_via_proxy(proxy: &ProxyConfig, target: &str) -> Result<TcpStream> {
        let proxy_addr = format!("{}:{}", proxy.host, proxy.port);

        match proxy.proxy_type {
            ProxyType::Socks5 => {
                let stream = if let (Some(user), Some(pass)) = (&proxy.username, &proxy.password) {
                    Socks5Stream::connect_with_password(proxy_addr.as_str(), target, user, pass)
                        .await
                        .context("Failed to connect via SOCKS5 proxy")?
                } else {
                    Socks5Stream::connect(proxy_addr.as_str(), target)
                        .await
                        .context("Failed to connect via SOCKS5 proxy")?
                };
                Ok(stream.into_inner())
            }
            ProxyType::Http => {
                let mut stream = TcpStream::connect(&proxy_addr)
                    .await
                    .context("Failed to connect to HTTP proxy")?;

                let connect_request = if let (Some(user), Some(pass)) =
                    (&proxy.username, &proxy.password)
                {
                    let credentials = base64::Engine::encode(
                        &base64::engine::general_purpose::STANDARD,
                        format!("{}:{}", user, pass),
                    );
                    format!(
                        "CONNECT {} HTTP/1.1\r\nHost: {}\r\nProxy-Authorization: Basic {}\r\n\r\n",
                        target, target, credentials
                    )
                } else {
                    format!("CONNECT {} HTTP/1.1\r\nHost: {}\r\n\r\n", target, target)
                };

                stream.write_all(connect_request.as_bytes()).await?;

                let mut response = vec![0u8; 1024];
                let n = stream.read(&mut response).await?;
                let response_str = String::from_utf8_lossy(&response[..n]);

                if !response_str.contains("200") {
                    anyhow::bail!("HTTP proxy connection failed: {}", response_str);
                }

                Ok(stream)
            }
        }
    }

    async fn authenticate(
        handle: &mut client::Handle<SftpHandler>,
        server: &ServerConfig,
    ) -> Result<()> {
        match &server.auth {
            AuthMethod::Password(password) => {
                let auth_result = handle
                    .authenticate_password(&server.username, password)
                    .await
                    .context("Password authentication failed")?;

                if !auth_result {
                    anyhow::bail!("Invalid username or password");
                }
            }
            AuthMethod::PrivateKey { key, passphrase } => {
                let key_pair = if let Some(passphrase) = passphrase {
                    decode_secret_key(key, Some(passphrase))
                        .context("Failed to decode private key with passphrase")?
                } else {
                    decode_secret_key(key, None).context("Failed to decode private key")?
                };

                let auth_result = handle
                    .authenticate_publickey(&server.username, Arc::new(key_pair))
                    .await
                    .context("Public key authentication failed")?;

                if !auth_result {
                    anyhow::bail!("Public key authentication rejected");
                }
            }
        }

        Ok(())
    }

    pub async fn list_dir(&self, path: &str) -> Result<Vec<FileEntry>> {
        let dir = self.sftp.read_dir(path).await?;
        let mut entries = Vec::new();

        for entry in dir {
            let metadata = entry.metadata();
            let file_type = metadata.file_type();
            let is_dir = file_type.is_dir();

            let permissions = format!(
                "{}{}{}{}{}{}{}{}{}",
                if is_dir { 'd' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o400 != 0) { 'r' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o200 != 0) { 'w' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o100 != 0) { 'x' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o040 != 0) { 'r' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o020 != 0) { 'w' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o010 != 0) { 'x' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o004 != 0) { 'r' } else { '-' },
                if metadata.permissions.map_or(false, |p| p & 0o002 != 0) { 'w' } else { '-' },
            );

            entries.push(FileEntry {
                name: entry.file_name(),
                path: format!("{}/{}", path.trim_end_matches('/'), entry.file_name()),
                is_dir,
                size: metadata.size.unwrap_or(0),
                modified: metadata.mtime.unwrap_or(0) as i64,
                permissions,
            });
        }

        // Sort: directories first, then by name
        entries.sort_by(|a, b| {
            match (a.is_dir, b.is_dir) {
                (true, false) => std::cmp::Ordering::Less,
                (false, true) => std::cmp::Ordering::Greater,
                _ => a.name.to_lowercase().cmp(&b.name.to_lowercase()),
            }
        });

        Ok(entries)
    }

    pub async fn read_file(&self, path: &str) -> Result<Vec<u8>> {
        let mut file = self.sftp.open(path).await?;
        let mut contents = Vec::new();
        file.read_to_end(&mut contents).await?;
        Ok(contents)
    }

    pub async fn write_file(&self, path: &str, contents: &[u8]) -> Result<()> {
        let mut file = self.sftp.create(path).await?;
        file.write_all(contents).await?;
        Ok(())
    }

    pub async fn delete(&self, path: &str, is_dir: bool) -> Result<()> {
        if is_dir {
            self.sftp.remove_dir(path).await?;
        } else {
            self.sftp.remove_file(path).await?;
        }
        Ok(())
    }

    pub async fn rename(&self, old_path: &str, new_path: &str) -> Result<()> {
        self.sftp.rename(old_path, new_path).await?;
        Ok(())
    }

    pub async fn create_dir(&self, path: &str) -> Result<()> {
        self.sftp.create_dir(path).await?;
        Ok(())
    }

    pub fn session_id(&self) -> &str {
        &self.session_id
    }

    pub async fn close(&self) -> Result<()> {
        self.sftp.close().await?;
        SFTP_SESSIONS.write().await.remove(&self.session_id);
        Ok(())
    }
}

pub async fn get_sftp_session(session_id: &str) -> Option<Arc<SftpConnection>> {
    SFTP_SESSIONS.read().await.get(session_id).cloned()
}

pub async fn remove_sftp_session(session_id: &str) {
    SFTP_SESSIONS.write().await.remove(session_id);
}

struct SftpHandler;

#[async_trait]
impl client::Handler for SftpHandler {
    type Error = anyhow::Error;

    async fn check_server_key(
        &mut self,
        _server_public_key: &key::PublicKey,
    ) -> Result<bool, Self::Error> {
        Ok(true)
    }
}
