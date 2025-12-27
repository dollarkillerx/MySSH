mod commands;
mod models;
mod sftp;
mod ssh;
mod storage;

use commands::*;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .invoke_handler(tauri::generate_handler![
            // Server management
            get_servers,
            get_server,
            save_server,
            delete_server,
            export_servers,
            import_servers,
            // SSH
            ssh_connect,
            ssh_write,
            ssh_resize,
            ssh_disconnect,
            // SFTP
            sftp_connect,
            sftp_list_dir,
            sftp_read_file,
            sftp_write_file,
            sftp_delete,
            sftp_rename,
            sftp_create_dir,
            sftp_disconnect,
            sftp_create_file,
            sftp_download,
            sftp_upload,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
