use crate::models::{AuthMethod, ProxyConfig, ProxyType, ServerConfig, TerminalSize};
use crate::storage;
use anyhow::{Context, Result};
use async_trait::async_trait;
use once_cell::sync::Lazy;
use russh::keys::*;
use russh::*;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::{mpsc, Mutex, RwLock};
use tokio_socks::tcp::Socks5Stream;

static SESSIONS: Lazy<RwLock<HashMap<String, Arc<SshSession>>>> =
    Lazy::new(|| RwLock::new(HashMap::new()));

// Store jump host connections to keep them alive
static JUMP_CONNECTIONS: Lazy<RwLock<HashMap<String, Arc<JumpHostConnection>>>> =
    Lazy::new(|| RwLock::new(HashMap::new()));

struct JumpHostConnection {
    #[allow(dead_code)]
    handle: client::Handle<JumpHostHandler>,
}

pub struct SshSession {
    session_id: String,
    handle: client::Handle<ClientHandler>,
    channel: Mutex<Option<Channel<client::Msg>>>,
    #[allow(dead_code)]
    output_tx: mpsc::UnboundedSender<Vec<u8>>,
    output_rx: Mutex<Option<mpsc::UnboundedReceiver<Vec<u8>>>>,
    // Keep jump host connection alive
    #[allow(dead_code)]
    jump_connection_id: Option<String>,
}

impl SshSession {
    pub async fn connect(server: &ServerConfig) -> Result<Arc<Self>> {
        let session_id = uuid::Uuid::new_v4().to_string();

        // Check if we need to use a jump host
        let (stream, jump_connection_id): (Box<dyn AsyncReadWrite>, Option<String>) =
            if let Some(jump_host_id) = &server.jump_host {
                let (stream, conn_id) = Self::connect_via_jump_host(jump_host_id, server).await?;
                (Box::new(stream), Some(conn_id))
            } else {
                let stream = Self::create_connection(server).await?;
                (Box::new(stream), None)
            };

        // SSH config
        let config = client::Config {
            inactivity_timeout: Some(std::time::Duration::from_secs(3600)),
            ..Default::default()
        };

        let config = Arc::new(config);
        let (output_tx, output_rx) = mpsc::unbounded_channel();

        let handler = ClientHandler {
            output_tx: output_tx.clone(),
        };

        let handle = client::connect_stream(config, stream, handler)
            .await
            .context("Failed to establish SSH connection")?;

        // Authenticate
        let mut handle = handle;
        Self::authenticate(&mut handle, server).await?;

        let session = Arc::new(Self {
            session_id: session_id.clone(),
            handle,
            channel: Mutex::new(None),
            output_tx,
            output_rx: Mutex::new(Some(output_rx)),
            jump_connection_id,
        });

        SESSIONS.write().await.insert(session_id, session.clone());

        Ok(session)
    }

    async fn connect_via_jump_host(
        jump_host_id: &str,
        target_server: &ServerConfig,
    ) -> Result<(ChannelStream<client::Msg>, String)> {
        // Get jump host server config
        let jump_server = storage::get_server(jump_host_id)
            .context("Jump host server not found")?;

        // Connect to jump host
        let jump_stream = Self::create_connection(&jump_server).await?;

        let config = client::Config {
            inactivity_timeout: Some(std::time::Duration::from_secs(3600)),
            ..Default::default()
        };
        let config = Arc::new(config);

        let handler = JumpHostHandler;
        let mut jump_handle = client::connect_stream(config, jump_stream, handler)
            .await
            .context("Failed to connect to jump host")?;

        // Authenticate to jump host
        Self::authenticate_jump_host(&mut jump_handle, &jump_server).await?;

        // Open direct-tcpip channel to target server
        let channel = jump_handle
            .channel_open_direct_tcpip(
                &target_server.host,
                target_server.port as u32,
                "127.0.0.1",
                0,
            )
            .await
            .context("Failed to open tunnel through jump host")?;

        // Store jump connection to keep it alive
        let conn_id = uuid::Uuid::new_v4().to_string();
        let jump_conn = Arc::new(JumpHostConnection {
            handle: jump_handle,
        });
        JUMP_CONNECTIONS.write().await.insert(conn_id.clone(), jump_conn);

        Ok((channel.into_stream(), conn_id))
    }

    async fn authenticate_jump_host(
        handle: &mut client::Handle<JumpHostHandler>,
        server: &ServerConfig,
    ) -> Result<()> {
        match &server.auth {
            AuthMethod::Password(password) => {
                let auth_result = handle
                    .authenticate_password(&server.username, password)
                    .await
                    .context("Jump host password authentication failed")?;

                if !auth_result {
                    anyhow::bail!("Jump host: Invalid username or password");
                }
            }
            AuthMethod::PrivateKey { key, passphrase } => {
                let passphrase_opt = passphrase.as_ref().filter(|p| !p.is_empty());
                let key_pair = if let Some(passphrase) = passphrase_opt {
                    decode_secret_key(key, Some(passphrase))
                        .context("Failed to decode jump host private key with passphrase")?
                } else {
                    decode_secret_key(key, None)
                        .context("Failed to decode jump host private key")?
                };

                let auth_result = handle
                    .authenticate_publickey(&server.username, Arc::new(key_pair))
                    .await
                    .context("Jump host public key authentication failed")?;

                if !auth_result {
                    anyhow::bail!("Jump host: Public key authentication rejected");
                }
            }
        }
        Ok(())
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
                    Socks5Stream::connect_with_password(
                        proxy_addr.as_str(),
                        target,
                        user,
                        pass,
                    )
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
                // HTTP CONNECT proxy
                let mut stream = TcpStream::connect(&proxy_addr)
                    .await
                    .context("Failed to connect to HTTP proxy")?;

                let connect_request = if let (Some(user), Some(pass)) = (&proxy.username, &proxy.password) {
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

    async fn authenticate(handle: &mut client::Handle<ClientHandler>, server: &ServerConfig) -> Result<()> {
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
                // Treat empty passphrase as None
                let passphrase_opt = passphrase.as_ref().filter(|p| !p.is_empty());
                let key_pair = if let Some(passphrase) = passphrase_opt {
                    decode_secret_key(key, Some(passphrase))
                        .context("Failed to decode private key with passphrase")?
                } else {
                    decode_secret_key(key, None)
                        .context("Failed to decode private key")?
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

    pub async fn open_shell(&self, size: TerminalSize) -> Result<()> {
        let channel = self
            .handle
            .channel_open_session()
            .await
            .context("Failed to open channel")?;

        channel
            .request_pty(
                false,
                "xterm-256color",
                size.cols,
                size.rows,
                0,
                0,
                &[],
            )
            .await
            .context("Failed to request PTY")?;

        channel
            .request_shell(false)
            .await
            .context("Failed to request shell")?;

        *self.channel.lock().await = Some(channel);

        Ok(())
    }

    pub async fn write(&self, data: &[u8]) -> Result<()> {
        let channel_guard = self.channel.lock().await;
        if let Some(channel) = channel_guard.as_ref() {
            channel.data(data).await?;
        }
        Ok(())
    }

    pub async fn resize(&self, size: TerminalSize) -> Result<()> {
        let channel_guard = self.channel.lock().await;
        if let Some(channel) = channel_guard.as_ref() {
            channel
                .window_change(size.cols, size.rows, 0, 0)
                .await?;
        }
        Ok(())
    }

    pub async fn take_output_receiver(&self) -> Option<mpsc::UnboundedReceiver<Vec<u8>>> {
        self.output_rx.lock().await.take()
    }

    pub fn session_id(&self) -> &str {
        &self.session_id
    }

    pub async fn close(&self) -> Result<()> {
        let channel = self.channel.lock().await.take();
        if let Some(channel) = channel {
            channel.eof().await.ok();
            channel.close().await.ok();
        }
        self.handle.disconnect(Disconnect::ByApplication, "", "en").await?;
        SESSIONS.write().await.remove(&self.session_id);

        // Clean up jump connection if exists
        if let Some(conn_id) = &self.jump_connection_id {
            JUMP_CONNECTIONS.write().await.remove(conn_id);
        }

        Ok(())
    }
}

pub async fn get_session(session_id: &str) -> Option<Arc<SshSession>> {
    SESSIONS.read().await.get(session_id).cloned()
}

pub async fn remove_session(session_id: &str) {
    if let Some(session) = SESSIONS.write().await.remove(session_id) {
        // Clean up jump connection if exists
        if let Some(conn_id) = &session.jump_connection_id {
            JUMP_CONNECTIONS.write().await.remove(conn_id);
        }
    }
}

// Trait for async read/write streams
trait AsyncReadWrite: AsyncRead + AsyncWrite + Unpin + Send {}
impl<T: AsyncRead + AsyncWrite + Unpin + Send> AsyncReadWrite for T {}

struct ClientHandler {
    output_tx: mpsc::UnboundedSender<Vec<u8>>,
}

#[async_trait]
impl client::Handler for ClientHandler {
    type Error = anyhow::Error;

    async fn check_server_key(
        &mut self,
        _server_public_key: &key::PublicKey,
    ) -> Result<bool, Self::Error> {
        // TODO: Implement proper host key verification
        Ok(true)
    }

    async fn data(
        &mut self,
        _channel: ChannelId,
        data: &[u8],
        _session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        self.output_tx.send(data.to_vec()).ok();
        Ok(())
    }

    async fn extended_data(
        &mut self,
        _channel: ChannelId,
        _ext: u32,
        data: &[u8],
        _session: &mut client::Session,
    ) -> Result<(), Self::Error> {
        self.output_tx.send(data.to_vec()).ok();
        Ok(())
    }
}

struct JumpHostHandler;

#[async_trait]
impl client::Handler for JumpHostHandler {
    type Error = anyhow::Error;

    async fn check_server_key(
        &mut self,
        _server_public_key: &key::PublicKey,
    ) -> Result<bool, Self::Error> {
        // TODO: Implement proper host key verification
        Ok(true)
    }
}
