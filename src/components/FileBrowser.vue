<script setup>
import { ref, onMounted, onUnmounted, watch, computed } from "vue";
import { useI18n } from "vue-i18n";
import { save, open } from "@tauri-apps/plugin-dialog";
import {
  sftpConnect,
  sftpListDir,
  sftpDelete,
  sftpRename,
  sftpCreateDir,
  sftpDisconnect,
  sftpCreateFile,
  sftpReadFile,
  sftpWriteFile,
  sftpDownload,
  sftpUpload,
} from "../composables/useApi";

const { t } = useI18n();

const props = defineProps({
  server: Object,
});

const emit = defineEmits(["close"]);

const status = ref("connecting");
const errorMessage = ref("");
const currentPath = ref("/");
const files = ref([]);
const loading = ref(false);
const selectedFile = ref(null);

// Editor state
const showEditor = ref(false);
const editorFile = ref(null);
const editorContent = ref("");
const editorLoading = ref(false);

// Delete confirm state
const showDeleteConfirm = ref(false);
const deleteTarget = ref(null);

// Upload state
const uploading = ref(false);
const uploadProgress = ref("");
const isDragging = ref(false);

// Input modal state (for rename, new file, new folder)
const showInputModal = ref(false);
const inputModalType = ref(""); // "rename" | "newFile" | "newFolder"
const inputModalValue = ref("");
const inputModalTarget = ref(null); // for rename, the file being renamed

let sessionId = null;

const pathParts = computed(() => {
  const parts = currentPath.value.split("/").filter(Boolean);
  return [{ name: "/", path: "/" }, ...parts.map((name, i) => ({
    name,
    path: "/" + parts.slice(0, i + 1).join("/"),
  }))];
});

async function connect() {
  if (!props.server) return;

  status.value = "connecting";
  errorMessage.value = "";

  try {
    sessionId = await sftpConnect(props.server.id);
    status.value = "connected";
    await loadDirectory("/");
  } catch (error) {
    status.value = "error";
    errorMessage.value = error.toString();
  }
}

async function disconnect() {
  if (sessionId) {
    await sftpDisconnect(sessionId);
    sessionId = null;
  }
}

async function loadDirectory(path) {
  if (!sessionId) return;

  loading.value = true;
  try {
    files.value = await sftpListDir(sessionId, path);
    currentPath.value = path;
    selectedFile.value = null;
  } catch (error) {
    alert(t("sftp.loadFailed", { error }));
  } finally {
    loading.value = false;
  }
}

async function navigateTo(path) {
  await loadDirectory(path);
}

async function openItem(file) {
  if (file.is_dir) {
    await loadDirectory(file.path);
  } else {
    selectedFile.value = file;
  }
}

async function goUp() {
  const parts = currentPath.value.split("/").filter(Boolean);
  if (parts.length > 0) {
    parts.pop();
    const parentPath = "/" + parts.join("/");
    await loadDirectory(parentPath || "/");
  }
}

async function refresh() {
  await loadDirectory(currentPath.value);
}

function openNewFolderModal() {
  inputModalType.value = "newFolder";
  inputModalValue.value = "";
  inputModalTarget.value = null;
  showInputModal.value = true;
}

async function doCreateFolder() {
  const name = inputModalValue.value.trim();
  if (!name) return;

  showInputModal.value = false;

  try {
    const newPath = currentPath.value === "/" ? `/${name}` : `${currentPath.value}/${name}`;
    await sftpCreateDir(sessionId, newPath);
    await refresh();
  } catch (error) {
    alert(t("sftp.createFailed", { error }));
  }
}

function confirmDelete(file) {
  deleteTarget.value = file;
  showDeleteConfirm.value = true;
}

async function doDelete() {
  if (!deleteTarget.value) return;

  const file = deleteTarget.value;
  showDeleteConfirm.value = false;

  try {
    await sftpDelete(sessionId, file.path, file.is_dir);
    await refresh();
  } catch (error) {
    alert(t("sftp.deleteFailed", { error }));
  } finally {
    deleteTarget.value = null;
  }
}

function cancelDelete() {
  showDeleteConfirm.value = false;
  deleteTarget.value = null;
}

function openRenameModal(file) {
  inputModalType.value = "rename";
  inputModalValue.value = file.name;
  inputModalTarget.value = file;
  showInputModal.value = true;
}

async function doRename() {
  const newName = inputModalValue.value.trim();
  const file = inputModalTarget.value;
  if (!newName || !file || newName === file.name) {
    showInputModal.value = false;
    return;
  }

  showInputModal.value = false;

  try {
    const dir = file.path.substring(0, file.path.lastIndexOf("/"));
    const newPath = dir ? `${dir}/${newName}` : `/${newName}`;
    await sftpRename(sessionId, file.path, newPath);
    await refresh();
  } catch (error) {
    alert(t("sftp.renameFailed", { error }));
  }
}

function openNewFileModal() {
  inputModalType.value = "newFile";
  inputModalValue.value = "";
  inputModalTarget.value = null;
  showInputModal.value = true;
}

async function doCreateFile() {
  const name = inputModalValue.value.trim();
  if (!name) return;

  showInputModal.value = false;

  try {
    const newPath = currentPath.value === "/" ? `/${name}` : `${currentPath.value}/${name}`;
    await sftpCreateFile(sessionId, newPath);
    await refresh();
  } catch (error) {
    alert(t("sftp.createFileFailed", { error }));
  }
}

function closeInputModal() {
  showInputModal.value = false;
  inputModalValue.value = "";
  inputModalTarget.value = null;
}

function submitInputModal() {
  if (inputModalType.value === "rename") {
    doRename();
  } else if (inputModalType.value === "newFile") {
    doCreateFile();
  } else if (inputModalType.value === "newFolder") {
    doCreateFolder();
  }
}

function getInputModalTitle() {
  if (inputModalType.value === "rename") return t("sftp.rename");
  if (inputModalType.value === "newFile") return t("sftp.newFile");
  if (inputModalType.value === "newFolder") return t("sftp.newFolder");
  return "";
}

function getInputModalLabel() {
  if (inputModalType.value === "rename") return t("sftp.newName");
  if (inputModalType.value === "newFile") return t("sftp.fileName");
  if (inputModalType.value === "newFolder") return t("sftp.folderName");
  return "";
}

async function editFile(file) {
  if (file.is_dir) return;

  editorFile.value = file;
  editorLoading.value = true;
  showEditor.value = true;

  try {
    const data = await sftpReadFile(sessionId, file.path);
    const decoder = new TextDecoder();
    editorContent.value = decoder.decode(new Uint8Array(data));
  } catch (error) {
    alert(t("sftp.readFailed", { error }));
    showEditor.value = false;
  } finally {
    editorLoading.value = false;
  }
}

async function saveFile() {
  if (!editorFile.value) return;

  editorLoading.value = true;
  try {
    const encoder = new TextEncoder();
    const data = encoder.encode(editorContent.value);
    await sftpWriteFile(sessionId, editorFile.value.path, data);
    showEditor.value = false;
    await refresh();
  } catch (error) {
    alert(t("sftp.saveFailed", { error }));
  } finally {
    editorLoading.value = false;
  }
}

function closeEditor() {
  showEditor.value = false;
  editorFile.value = null;
  editorContent.value = "";
}

async function downloadFile(file) {
  if (file.is_dir) return;

  try {
    const localPath = await save({
      defaultPath: file.name,
      title: t("sftp.saveAs"),
    });

    if (!localPath) return;

    await sftpDownload(sessionId, file.path, localPath);
    alert(t("sftp.downloadSuccess"));
  } catch (error) {
    alert(t("sftp.downloadFailed", { error }));
  }
}

async function uploadFile() {
  try {
    const localPath = await open({
      multiple: false,
      title: t("sftp.selectFile"),
    });

    if (!localPath) return;

    await doUpload(localPath);
  } catch (error) {
    alert(t("sftp.uploadFailed", { error }));
  }
}

async function doUpload(localPath) {
  const fileName = localPath.split("/").pop().split("\\").pop();
  const remotePath = currentPath.value === "/" ? `/${fileName}` : `${currentPath.value}/${fileName}`;

  uploading.value = true;
  uploadProgress.value = fileName;

  try {
    await sftpUpload(sessionId, localPath, remotePath);
    await refresh();
  } catch (error) {
    alert(t("sftp.uploadFailed", { error }));
  } finally {
    uploading.value = false;
    uploadProgress.value = "";
  }
}

// Drag and drop handlers
function onDragEnter(e) {
  e.preventDefault();
  isDragging.value = true;
}

function onDragLeave(e) {
  e.preventDefault();
  isDragging.value = false;
}

function onDragOver(e) {
  e.preventDefault();
}

async function onDrop(e) {
  e.preventDefault();
  isDragging.value = false;

  if (!sessionId || status.value !== "connected") return;

  const items = e.dataTransfer?.items;
  if (!items || items.length === 0) return;

  // Get file paths from dropped items
  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    if (item.kind === "file") {
      const file = item.getAsFile();
      if (file && file.path) {
        await doUpload(file.path);
      }
    }
  }
}

function formatSize(bytes) {
  if (bytes === 0) return "-";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return (bytes / Math.pow(1024, i)).toFixed(1) + " " + units[i];
}

function formatDate(timestamp) {
  if (!timestamp) return "-";
  return new Date(timestamp * 1000).toLocaleString();
}

watch(
  () => props.server?.id,
  async (newId, oldId) => {
    if (newId !== oldId) {
      await disconnect();
      if (newId) {
        await connect();
      }
    }
  }
);

onMounted(() => {
  if (props.server) {
    connect();
  }
});

onUnmounted(() => {
  disconnect();
});
</script>

<template>
  <div class="file-browser">
    <div class="browser-header">
      <div class="browser-title">
        <span class="status-dot" :class="status"></span>
        {{ t("sftp.title") }} - {{ server?.name }}
      </div>
      <div class="browser-actions">
        <button class="action-btn" @click="$emit('close')" :title="t('common.close')">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <line x1="18" y1="6" x2="6" y2="18"></line>
            <line x1="6" y1="6" x2="18" y2="18"></line>
          </svg>
        </button>
      </div>
    </div>

    <div v-if="status === 'connecting'" class="browser-status">
      <div class="spinner"></div>
      <span>{{ t("sftp.connecting") }}</span>
    </div>

    <div v-else-if="status === 'error'" class="browser-status error">
      <span>{{ errorMessage }}</span>
      <button class="btn" @click="connect">{{ t("terminal.retry") }}</button>
    </div>

    <template v-else>
      <div class="toolbar">
        <button class="tool-btn" @click="goUp" :disabled="currentPath === '/'" :title="t('sftp.goUp')">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M15 18l-6-6 6-6"></path>
          </svg>
        </button>
        <div class="breadcrumb">
          <span
            v-for="(part, i) in pathParts"
            :key="part.path"
            class="breadcrumb-item"
            @click="navigateTo(part.path)"
          >
            <span v-if="i > 0" class="separator">/</span>
            {{ part.name }}
          </span>
        </div>
        <div class="toolbar-right">
          <button class="tool-btn" @click="refresh" :title="t('sftp.refresh')">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="23 4 23 10 17 10"></polyline>
              <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"></path>
            </svg>
          </button>
          <button class="tool-btn" @click="uploadFile" :title="t('sftp.upload')">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
              <polyline points="17 8 12 3 7 8"></polyline>
              <line x1="12" y1="3" x2="12" y2="15"></line>
            </svg>
          </button>
          <button class="tool-btn" @click="openNewFileModal" :title="t('sftp.newFile')">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"></path>
              <polyline points="14 2 14 8 20 8"></polyline>
              <line x1="12" y1="18" x2="12" y2="12"></line>
              <line x1="9" y1="15" x2="15" y2="15"></line>
            </svg>
          </button>
          <button class="tool-btn" @click="openNewFolderModal" :title="t('sftp.newFolder')">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path>
              <line x1="12" y1="11" x2="12" y2="17"></line>
              <line x1="9" y1="14" x2="15" y2="14"></line>
            </svg>
          </button>
        </div>
      </div>

      <div
        class="file-list"
        :class="{ loading, dragging: isDragging }"
        @dragenter="onDragEnter"
        @dragleave="onDragLeave"
        @dragover="onDragOver"
        @drop="onDrop"
      >
        <div
          v-for="file in files"
          :key="file.path"
          class="file-item"
          :class="{ selected: selectedFile?.path === file.path, directory: file.is_dir }"
          @click="selectedFile = file"
          @dblclick="openItem(file)"
        >
          <div class="file-icon">
            <svg v-if="file.is_dir" width="20" height="20" viewBox="0 0 24 24" fill="currentColor">
              <path d="M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"/>
            </svg>
            <svg v-else width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"></path>
              <polyline points="13 2 13 9 20 9"></polyline>
            </svg>
          </div>
          <div class="file-info">
            <div class="file-name">{{ file.name }}</div>
            <div class="file-meta">
              <span>{{ formatSize(file.size) }}</span>
              <span>{{ formatDate(file.modified) }}</span>
            </div>
          </div>
          <div class="file-actions">
            <button v-if="!file.is_dir" class="action-btn small" @click.stop="editFile(file)" :title="t('sftp.edit')">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"></path>
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"></path>
              </svg>
            </button>
            <button v-if="!file.is_dir" class="action-btn small" @click.stop="downloadFile(file)" :title="t('sftp.download')">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                <polyline points="7 10 12 15 17 10"></polyline>
                <line x1="12" y1="15" x2="12" y2="3"></line>
              </svg>
            </button>
            <button class="action-btn small" @click.stop="openRenameModal(file)" :title="t('sftp.rename')">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z"></path>
              </svg>
            </button>
            <button class="action-btn small delete" @click.stop="confirmDelete(file)" :title="t('sftp.delete')">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="3 6 5 6 21 6"></polyline>
                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
              </svg>
            </button>
          </div>
        </div>

        <div v-if="files.length === 0 && !loading" class="empty">
          {{ t("sftp.emptyFolder") }}
        </div>
      </div>
    </template>

    <!-- File Editor Modal -->
    <div v-if="showEditor" class="editor-overlay" @click.self="closeEditor">
      <div class="editor-modal">
        <div class="editor-header">
          <span class="editor-title">{{ editorFile?.name }}</span>
          <div class="editor-actions">
            <button class="btn primary" @click="saveFile" :disabled="editorLoading">
              {{ editorLoading ? t("sftp.saving") : t("sftp.save") }}
            </button>
            <button class="btn" @click="closeEditor">{{ t("common.close") }}</button>
          </div>
        </div>
        <div class="editor-body">
          <div v-if="editorLoading" class="editor-loading">
            <div class="spinner"></div>
          </div>
          <textarea
            v-else
            v-model="editorContent"
            class="editor-content"
            spellcheck="false"
          ></textarea>
        </div>
      </div>
    </div>

    <!-- Delete Confirmation Modal -->
    <div v-if="showDeleteConfirm" class="confirm-overlay" @click.self="cancelDelete">
      <div class="confirm-modal">
        <div class="confirm-icon">
          <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="10"></circle>
            <line x1="12" y1="8" x2="12" y2="12"></line>
            <line x1="12" y1="16" x2="12.01" y2="16"></line>
          </svg>
        </div>
        <div class="confirm-message">{{ t("sftp.deleteConfirm", { name: deleteTarget?.name }) }}</div>
        <div class="confirm-actions">
          <button class="btn" @click="cancelDelete">{{ t("common.cancel") }}</button>
          <button class="btn danger" @click="doDelete">{{ t("sftp.delete") }}</button>
        </div>
      </div>
    </div>

    <!-- Upload Progress -->
    <div v-if="uploading" class="upload-overlay">
      <div class="upload-modal">
        <div class="spinner"></div>
        <div class="upload-text">{{ t("sftp.uploading") }}</div>
        <div class="upload-file">{{ uploadProgress }}</div>
      </div>
    </div>

    <!-- Drag Overlay -->
    <div v-if="isDragging" class="drag-overlay">
      <div class="drag-content">
        <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
          <polyline points="17 8 12 3 7 8"></polyline>
          <line x1="12" y1="3" x2="12" y2="15"></line>
        </svg>
        <span>{{ t("sftp.dropToUpload") }}</span>
      </div>
    </div>

    <!-- Input Modal (Rename / New File / New Folder) -->
    <div v-if="showInputModal" class="input-overlay" @click.self="closeInputModal">
      <div class="input-modal">
        <div class="input-header">{{ getInputModalTitle() }}</div>
        <div class="input-body">
          <label class="input-label">{{ getInputModalLabel() }}</label>
          <input
            type="text"
            v-model="inputModalValue"
            class="input-field"
            @keyup.enter="submitInputModal"
            ref="inputFieldRef"
            autofocus
          />
        </div>
        <div class="input-actions">
          <button class="btn" @click="closeInputModal">{{ t("common.cancel") }}</button>
          <button class="btn primary" @click="submitInputModal">{{ t("common.confirm") }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.file-browser {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #1e1e2e;
}

.browser-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 16px;
  background: #181825;
  border-bottom: 1px solid #313244;
}

.browser-title {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #cdd6f4;
  font-size: 14px;
  font-weight: 500;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #6c7086;
}

.status-dot.connecting {
  background: #f9e2af;
  animation: pulse 1s infinite;
}

.status-dot.connected {
  background: #a6e3a1;
}

.status-dot.error {
  background: #f38ba8;
}

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.5; }
}

.browser-status {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 16px;
  color: #a6adc8;
}

.browser-status.error {
  color: #f38ba8;
}

.spinner {
  width: 32px;
  height: 32px;
  border: 3px solid #313244;
  border-top-color: #89b4fa;
  border-radius: 50%;
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.btn {
  padding: 8px 16px;
  border-radius: 6px;
  border: none;
  background: #89b4fa;
  color: #1e1e2e;
  font-size: 14px;
  cursor: pointer;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #181825;
  border-bottom: 1px solid #313244;
}

.tool-btn {
  width: 32px;
  height: 32px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: #a6adc8;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
}

.tool-btn:hover {
  background: #313244;
  color: #cdd6f4;
}

.tool-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.breadcrumb {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 2px;
  overflow-x: auto;
  color: #a6adc8;
  font-size: 13px;
}

.breadcrumb-item {
  cursor: pointer;
  white-space: nowrap;
}

.breadcrumb-item:hover {
  color: #89b4fa;
}

.separator {
  margin: 0 4px;
  color: #6c7086;
}

.toolbar-right {
  display: flex;
  gap: 4px;
}

.file-list {
  flex: 1;
  overflow-y: auto;
  padding: 8px;
}

.file-list.loading {
  opacity: 0.5;
  pointer-events: none;
}

.file-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.15s;
}

.file-item:hover {
  background: #313244;
}

.file-item.selected {
  background: #45475a;
}

.file-icon {
  width: 20px;
  height: 20px;
  color: #6c7086;
}

.file-item.directory .file-icon {
  color: #f9e2af;
}

.file-info {
  flex: 1;
  min-width: 0;
}

.file-name {
  color: #cdd6f4;
  font-size: 14px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.file-meta {
  display: flex;
  gap: 16px;
  font-size: 11px;
  color: #6c7086;
  margin-top: 2px;
}

.file-actions {
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.15s;
}

.file-item:hover .file-actions {
  opacity: 1;
}

.action-btn {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: #a6adc8;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
}

.action-btn.small {
  width: 24px;
  height: 24px;
}

.action-btn:hover {
  background: #585b70;
  color: #cdd6f4;
}

.action-btn.delete:hover {
  background: #f38ba8;
  color: #1e1e2e;
}

.empty {
  text-align: center;
  padding: 40px;
  color: #6c7086;
}

/* Editor Modal */
.editor-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.editor-modal {
  width: 80%;
  height: 80%;
  max-width: 1000px;
  background: #1e1e2e;
  border-radius: 12px;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
}

.editor-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid #313244;
}

.editor-title {
  color: #cdd6f4;
  font-size: 14px;
  font-weight: 500;
}

.editor-actions {
  display: flex;
  gap: 8px;
}

.editor-body {
  flex: 1;
  display: flex;
  overflow: hidden;
}

.editor-loading {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
}

.editor-content {
  flex: 1;
  padding: 16px;
  background: #181825;
  border: none;
  color: #cdd6f4;
  font-family: 'Menlo', 'Monaco', 'Courier New', monospace;
  font-size: 13px;
  line-height: 1.5;
  resize: none;
  outline: none;
}

.btn {
  padding: 8px 16px;
  border-radius: 6px;
  border: none;
  font-size: 13px;
  cursor: pointer;
  background: #45475a;
  color: #cdd6f4;
}

.btn:hover {
  background: #585b70;
}

.btn.primary {
  background: #89b4fa;
  color: #1e1e2e;
}

.btn.primary:hover {
  background: #b4befe;
}

.btn.primary:disabled {
  background: #45475a;
  color: #6c7086;
  cursor: not-allowed;
}

.btn.danger {
  background: #f38ba8;
  color: #1e1e2e;
}

.btn.danger:hover {
  background: #eba0ac;
}

/* Delete Confirmation Modal */
.confirm-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.confirm-modal {
  background: #1e1e2e;
  border-radius: 12px;
  padding: 24px;
  text-align: center;
  min-width: 300px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
}

.confirm-icon {
  color: #f38ba8;
  margin-bottom: 16px;
}

.confirm-message {
  color: #cdd6f4;
  font-size: 14px;
  margin-bottom: 20px;
}

.confirm-actions {
  display: flex;
  justify-content: center;
  gap: 12px;
}

/* Upload Progress */
.upload-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.upload-modal {
  background: #1e1e2e;
  border-radius: 12px;
  padding: 32px;
  text-align: center;
  min-width: 200px;
}

.upload-text {
  color: #cdd6f4;
  font-size: 14px;
  margin-top: 16px;
}

.upload-file {
  color: #6c7086;
  font-size: 12px;
  margin-top: 8px;
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Drag Overlay */
.drag-overlay {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(137, 180, 250, 0.1);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
  pointer-events: none;
}

.drag-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  color: #89b4fa;
  font-size: 16px;
}

.file-list.dragging {
  border: 2px dashed #89b4fa;
  border-radius: 8px;
  margin: 8px;
}

/* Input Modal */
.input-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.input-modal {
  background: #1e1e2e;
  border-radius: 12px;
  padding: 24px;
  min-width: 320px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
}

.input-header {
  color: #cdd6f4;
  font-size: 16px;
  font-weight: 500;
  margin-bottom: 16px;
}

.input-body {
  margin-bottom: 20px;
}

.input-label {
  display: block;
  color: #a6adc8;
  font-size: 13px;
  margin-bottom: 8px;
}

.input-field {
  width: 100%;
  padding: 10px 12px;
  border-radius: 6px;
  border: 1px solid #313244;
  background: #181825;
  color: #cdd6f4;
  font-size: 14px;
  outline: none;
}

.input-field:focus {
  border-color: #89b4fa;
}

.input-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}
</style>
