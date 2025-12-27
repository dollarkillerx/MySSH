use crate::models::ServerConfig;
use aes_gcm::{
    aead::{Aead, KeyInit},
    Aes256Gcm, Nonce,
};
use anyhow::{Context, Result};
use base64::{engine::general_purpose::STANDARD as BASE64, Engine};
use once_cell::sync::Lazy;
use parking_lot::RwLock;
use rand::Rng;
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;

static STORAGE: Lazy<RwLock<Storage>> = Lazy::new(|| RwLock::new(Storage::new()));

pub struct Storage {
    servers: HashMap<String, ServerConfig>,
    data_dir: PathBuf,
    encryption_key: [u8; 32],
}

impl Storage {
    fn new() -> Self {
        let data_dir = dirs::data_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("myssh");

        fs::create_dir_all(&data_dir).ok();

        // Generate or load encryption key based on machine ID
        let encryption_key = Self::get_or_create_key(&data_dir);

        let mut storage = Self {
            servers: HashMap::new(),
            data_dir,
            encryption_key,
        };

        storage.load().ok();
        storage
    }

    fn get_or_create_key(data_dir: &PathBuf) -> [u8; 32] {
        let key_file = data_dir.join(".key");

        if let Ok(key_data) = fs::read(&key_file) {
            if key_data.len() == 32 {
                let mut key = [0u8; 32];
                key.copy_from_slice(&key_data);
                return key;
            }
        }

        // Generate new key
        let mut key = [0u8; 32];
        rand::thread_rng().fill(&mut key);
        fs::write(&key_file, &key).ok();

        // Set restrictive permissions on Unix
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&key_file, fs::Permissions::from_mode(0o600)).ok();
        }

        key
    }

    fn encrypt(&self, plaintext: &str) -> Result<String> {
        let cipher = Aes256Gcm::new_from_slice(&self.encryption_key)
            .context("Failed to create cipher")?;

        let mut nonce_bytes = [0u8; 12];
        rand::thread_rng().fill(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let ciphertext = cipher
            .encrypt(nonce, plaintext.as_bytes())
            .map_err(|e| anyhow::anyhow!("Encryption failed: {}", e))?;

        // Combine nonce + ciphertext and encode as base64
        let mut combined = nonce_bytes.to_vec();
        combined.extend(ciphertext);

        Ok(BASE64.encode(combined))
    }

    fn decrypt(&self, encrypted: &str) -> Result<String> {
        let combined = BASE64.decode(encrypted)
            .context("Failed to decode base64")?;

        if combined.len() < 12 {
            anyhow::bail!("Invalid encrypted data");
        }

        let (nonce_bytes, ciphertext) = combined.split_at(12);
        let nonce = Nonce::from_slice(nonce_bytes);

        let cipher = Aes256Gcm::new_from_slice(&self.encryption_key)
            .context("Failed to create cipher")?;

        let plaintext = cipher
            .decrypt(nonce, ciphertext)
            .map_err(|e| anyhow::anyhow!("Decryption failed: {}", e))?;

        String::from_utf8(plaintext).context("Invalid UTF-8")
    }

    fn servers_file(&self) -> PathBuf {
        self.data_dir.join("servers.json")
    }

    fn load(&mut self) -> Result<()> {
        let path = self.servers_file();
        if !path.exists() {
            return Ok(());
        }

        let content = fs::read_to_string(&path)?;
        let encrypted_servers: HashMap<String, EncryptedServerConfig> =
            serde_json::from_str(&content)?;

        self.servers.clear();
        for (id, encrypted) in encrypted_servers {
            if let Ok(server) = self.decrypt_server(&encrypted) {
                self.servers.insert(id, server);
            }
        }

        Ok(())
    }

    fn save(&self) -> Result<()> {
        let mut encrypted_servers: HashMap<String, EncryptedServerConfig> = HashMap::new();

        for (id, server) in &self.servers {
            if let Ok(encrypted) = self.encrypt_server(server) {
                encrypted_servers.insert(id.clone(), encrypted);
            }
        }

        let content = serde_json::to_string_pretty(&encrypted_servers)?;
        fs::write(self.servers_file(), content)?;

        Ok(())
    }

    fn encrypt_server(&self, server: &ServerConfig) -> Result<EncryptedServerConfig> {
        use crate::models::AuthMethod;

        let encrypted_auth = match &server.auth {
            AuthMethod::Password(pwd) => {
                EncryptedAuth::Password(self.encrypt(pwd)?)
            }
            AuthMethod::PrivateKey { key, passphrase } => {
                EncryptedAuth::PrivateKey {
                    key: self.encrypt(key)?,
                    passphrase: passphrase.as_ref().map(|p| self.encrypt(p)).transpose()?,
                }
            }
        };

        let encrypted_proxy = server.proxy.as_ref().map(|p| {
            EncryptedProxy {
                proxy_type: p.proxy_type.clone(),
                host: p.host.clone(),
                port: p.port,
                username: p.username.clone(),
                password: p.password.as_ref().map(|pwd| self.encrypt(pwd)).transpose().ok().flatten(),
            }
        });

        Ok(EncryptedServerConfig {
            id: server.id.clone(),
            name: server.name.clone(),
            host: server.host.clone(),
            port: server.port,
            username: server.username.clone(),
            auth: encrypted_auth,
            proxy: encrypted_proxy,
            notes: server.notes.clone(),
            created_at: server.created_at,
            updated_at: server.updated_at,
        })
    }

    fn decrypt_server(&self, encrypted: &EncryptedServerConfig) -> Result<ServerConfig> {
        use crate::models::{AuthMethod, ProxyConfig};

        let auth = match &encrypted.auth {
            EncryptedAuth::Password(pwd) => {
                AuthMethod::Password(self.decrypt(pwd)?)
            }
            EncryptedAuth::PrivateKey { key, passphrase } => {
                AuthMethod::PrivateKey {
                    key: self.decrypt(key)?,
                    passphrase: passphrase.as_ref().map(|p| self.decrypt(p)).transpose()?,
                }
            }
        };

        let proxy = encrypted.proxy.as_ref().map(|p| {
            ProxyConfig {
                proxy_type: p.proxy_type.clone(),
                host: p.host.clone(),
                port: p.port,
                username: p.username.clone(),
                password: p.password.as_ref().map(|pwd| self.decrypt(pwd)).transpose().ok().flatten(),
            }
        });

        Ok(ServerConfig {
            id: encrypted.id.clone(),
            name: encrypted.name.clone(),
            host: encrypted.host.clone(),
            port: encrypted.port,
            username: encrypted.username.clone(),
            auth,
            proxy,
            notes: encrypted.notes.clone(),
            created_at: encrypted.created_at,
            updated_at: encrypted.updated_at,
        })
    }
}

#[derive(serde::Serialize, serde::Deserialize)]
struct EncryptedServerConfig {
    id: String,
    name: String,
    host: String,
    port: u16,
    username: String,
    auth: EncryptedAuth,
    proxy: Option<EncryptedProxy>,
    notes: Option<String>,
    created_at: i64,
    updated_at: i64,
}

#[derive(serde::Serialize, serde::Deserialize)]
#[serde(tag = "type", content = "value")]
enum EncryptedAuth {
    Password(String),
    PrivateKey {
        key: String,
        passphrase: Option<String>,
    },
}

#[derive(serde::Serialize, serde::Deserialize)]
struct EncryptedProxy {
    proxy_type: crate::models::ProxyType,
    host: String,
    port: u16,
    username: Option<String>,
    password: Option<String>,
}

// Public API
pub fn get_all_servers() -> Vec<ServerConfig> {
    STORAGE.read().servers.values().cloned().collect()
}

pub fn get_server(id: &str) -> Option<ServerConfig> {
    STORAGE.read().servers.get(id).cloned()
}

pub fn save_server(mut server: ServerConfig) -> Result<ServerConfig> {
    let mut storage = STORAGE.write();

    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64;

    server.updated_at = now;
    if server.created_at == 0 {
        server.created_at = now;
    }

    storage.servers.insert(server.id.clone(), server.clone());
    storage.save()?;

    Ok(server)
}

pub fn delete_server(id: &str) -> Result<()> {
    let mut storage = STORAGE.write();
    storage.servers.remove(id);
    storage.save()
}

/// Export all servers with password-based encryption
pub fn export_servers(password: &str) -> Result<String> {
    use sha2::{Digest, Sha256};

    let storage = STORAGE.read();
    let servers: Vec<ServerConfig> = storage.servers.values().cloned().collect();

    // Serialize servers to JSON
    let json = serde_json::to_string(&servers)?;

    // Derive key from password using SHA-256
    let mut hasher = Sha256::new();
    hasher.update(password.as_bytes());
    hasher.update(b"myssh-export-salt-v1"); // Salt
    let key: [u8; 32] = hasher.finalize().into();

    // Encrypt with AES-256-GCM
    let cipher = Aes256Gcm::new_from_slice(&key)
        .context("Failed to create cipher")?;

    let mut nonce_bytes = [0u8; 12];
    rand::thread_rng().fill(&mut nonce_bytes);
    let nonce = Nonce::from_slice(&nonce_bytes);

    let ciphertext = cipher
        .encrypt(nonce, json.as_bytes())
        .map_err(|e| anyhow::anyhow!("Encryption failed: {}", e))?;

    // Format: version|nonce|ciphertext (all base64 encoded)
    let mut combined = Vec::new();
    combined.push(1u8); // Version byte
    combined.extend(&nonce_bytes);
    combined.extend(ciphertext);

    Ok(BASE64.encode(combined))
}

/// Import servers from password-encrypted backup
pub fn import_servers(encrypted_data: &str, password: &str) -> Result<usize> {
    use sha2::{Digest, Sha256};

    let combined = BASE64.decode(encrypted_data)
        .context("Invalid backup format")?;

    if combined.len() < 14 {
        anyhow::bail!("Invalid backup data");
    }

    let version = combined[0];
    if version != 1 {
        anyhow::bail!("Unsupported backup version");
    }

    let nonce_bytes = &combined[1..13];
    let ciphertext = &combined[13..];
    let nonce = Nonce::from_slice(nonce_bytes);

    // Derive key from password
    let mut hasher = Sha256::new();
    hasher.update(password.as_bytes());
    hasher.update(b"myssh-export-salt-v1");
    let key: [u8; 32] = hasher.finalize().into();

    // Decrypt
    let cipher = Aes256Gcm::new_from_slice(&key)
        .context("Failed to create cipher")?;

    let plaintext = cipher
        .decrypt(nonce, ciphertext)
        .map_err(|_| anyhow::anyhow!("Decryption failed - incorrect password or corrupted data"))?;

    let json = String::from_utf8(plaintext)
        .context("Invalid data format")?;

    let servers: Vec<ServerConfig> = serde_json::from_str(&json)
        .context("Failed to parse backup data")?;

    let count = servers.len();
    let mut storage = STORAGE.write();

    for mut server in servers {
        // Generate new ID to avoid conflicts
        server.id = uuid::Uuid::new_v4().to_string();
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs() as i64;
        server.updated_at = now;
        storage.servers.insert(server.id.clone(), server);
    }

    storage.save()?;
    Ok(count)
}
