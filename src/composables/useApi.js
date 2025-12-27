import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

// Server Management
export async function getServers() {
  return await invoke("get_servers");
}

export async function getServer(id) {
  return await invoke("get_server", { id });
}

export async function saveServer(serverData) {
  return await invoke("save_server", { request: serverData });
}

export async function deleteServer(id) {
  return await invoke("delete_server", { id });
}

export async function exportServers(password) {
  return await invoke("export_servers", { password });
}

export async function importServers(encryptedData, password) {
  return await invoke("import_servers", { encryptedData, password });
}

// SSH
export async function sshConnect(serverId, cols, rows) {
  return await invoke("ssh_connect", { serverId, cols, rows });
}

export async function sshWrite(sessionId, data) {
  return await invoke("ssh_write", { sessionId, data: Array.from(data) });
}

export async function sshResize(sessionId, cols, rows) {
  return await invoke("ssh_resize", { sessionId, cols, rows });
}

export async function sshDisconnect(sessionId) {
  return await invoke("ssh_disconnect", { sessionId });
}

export function onSshData(sessionId, callback) {
  return listen(`ssh-data-${sessionId}`, (event) => {
    callback(new Uint8Array(event.payload));
  });
}

// SFTP
export async function sftpConnect(serverId) {
  return await invoke("sftp_connect", { serverId });
}

export async function sftpListDir(sessionId, path) {
  return await invoke("sftp_list_dir", { sessionId, path });
}

export async function sftpReadFile(sessionId, path) {
  return await invoke("sftp_read_file", { sessionId, path });
}

export async function sftpWriteFile(sessionId, path, contents) {
  return await invoke("sftp_write_file", { sessionId, path, contents: Array.from(contents) });
}

export async function sftpDelete(sessionId, path, isDir) {
  return await invoke("sftp_delete", { sessionId, path, isDir });
}

export async function sftpRename(sessionId, oldPath, newPath) {
  return await invoke("sftp_rename", { sessionId, oldPath, newPath });
}

export async function sftpCreateDir(sessionId, path) {
  return await invoke("sftp_create_dir", { sessionId, path });
}

export async function sftpDisconnect(sessionId) {
  return await invoke("sftp_disconnect", { sessionId });
}

export async function sftpCreateFile(sessionId, path) {
  return await invoke("sftp_create_file", { sessionId, path });
}

export async function sftpDownload(sessionId, remotePath, localPath) {
  return await invoke("sftp_download", { sessionId, remotePath, localPath });
}

export async function sftpUpload(sessionId, localPath, remotePath) {
  return await invoke("sftp_upload", { sessionId, localPath, remotePath });
}
