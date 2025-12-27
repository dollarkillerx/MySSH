<script setup>
import { ref, computed } from "vue";
import { useI18n } from "vue-i18n";
import { save, open } from "@tauri-apps/plugin-dialog";
import { writeTextFile, readTextFile } from "@tauri-apps/plugin-fs";
import { SUPPORTED_LOCALES, saveLocale, isAutoLocale } from "../i18n";
import { exportServers, importServers } from "../composables/useApi";

const props = defineProps({
  visible: Boolean,
});

const emit = defineEmits(["close", "refresh"]);

const { t, locale } = useI18n();

const selectedLocale = ref(isAutoLocale() ? "auto" : locale.value);
const showPasswordModal = ref(false);
const passwordModalMode = ref("export"); // "export" or "import"
const password = ref("");
const confirmPassword = ref("");
const importData = ref("");
const loading = ref(false);

const localeOptions = computed(() => [
  { code: "auto", name: t("settings.languageAuto") },
  ...SUPPORTED_LOCALES,
]);

function handleLocaleChange(code) {
  selectedLocale.value = code;
  if (code === "auto") {
    // Get system locale
    const lang = navigator.language || "en-US";
    const match = SUPPORTED_LOCALES.find(l => l.code === lang || l.code.startsWith(lang.split("-")[0]));
    locale.value = match ? match.code : "en-US";
  } else {
    locale.value = code;
  }
  saveLocale(code);
}

function startExport() {
  passwordModalMode.value = "export";
  password.value = "";
  confirmPassword.value = "";
  showPasswordModal.value = true;
}

async function startImport() {
  try {
    const filePath = await open({
      filters: [{ name: "Backup", extensions: ["myssh"] }],
      title: t("settings.selectBackupFile"),
    });

    if (!filePath) return;

    const data = await readTextFile(filePath);
    importData.value = data;
    passwordModalMode.value = "import";
    password.value = "";
    showPasswordModal.value = true;
  } catch (error) {
    alert(t("settings.importFailed", { error }));
  }
}

async function handlePasswordSubmit() {
  if (passwordModalMode.value === "export") {
    if (password.value !== confirmPassword.value) {
      alert(t("settings.passwordMismatch"));
      return;
    }
    if (password.value.length < 4) {
      alert(t("settings.passwordTooShort"));
      return;
    }

    loading.value = true;
    try {
      const encrypted = await exportServers(password.value);

      const filePath = await save({
        defaultPath: `myssh-backup-${new Date().toISOString().slice(0, 10)}.myssh`,
        filters: [{ name: "Backup", extensions: ["myssh"] }],
        title: t("settings.saveBackup"),
      });

      if (filePath) {
        await writeTextFile(filePath, encrypted);
        alert(t("settings.exportSuccess"));
      }
    } catch (error) {
      alert(t("settings.exportFailed", { error }));
    } finally {
      loading.value = false;
      showPasswordModal.value = false;
    }
  } else {
    // Import
    if (password.value.length < 1) {
      alert(t("settings.enterPassword"));
      return;
    }

    loading.value = true;
    try {
      const count = await importServers(importData.value, password.value);
      alert(t("settings.importSuccess", { count }));
      emit("refresh");
    } catch (error) {
      alert(t("settings.importFailed", { error }));
    } finally {
      loading.value = false;
      showPasswordModal.value = false;
      importData.value = "";
    }
  }
}

function closePasswordModal() {
  showPasswordModal.value = false;
  password.value = "";
  confirmPassword.value = "";
  importData.value = "";
}
</script>

<template>
  <div v-if="visible" class="modal-overlay" @click.self="$emit('close')">
    <div class="modal settings-modal">
      <div class="modal-header">
        <h3>{{ t("settings.title") }}</h3>
        <button class="close-btn" @click="$emit('close')">&times;</button>
      </div>

      <div class="modal-body">
        <div class="settings-section">
          <h4>{{ t("settings.language") }}</h4>
          <div class="language-options">
            <label
              v-for="opt in localeOptions"
              :key="opt.code"
              class="language-option"
              :class="{ active: selectedLocale === opt.code }"
            >
              <input
                type="radio"
                :value="opt.code"
                v-model="selectedLocale"
                @change="handleLocaleChange(opt.code)"
              />
              <span class="option-label">{{ opt.name }}</span>
              <span v-if="opt.code === 'auto'" class="option-hint">
                ({{ SUPPORTED_LOCALES.find(l => l.code === locale)?.name }})
              </span>
            </label>
          </div>
        </div>

        <div class="settings-section">
          <h4>{{ t("settings.backup") }}</h4>
          <div class="backup-actions">
            <button class="btn" @click="startExport">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                <polyline points="17 8 12 3 7 8"></polyline>
                <line x1="12" y1="3" x2="12" y2="15"></line>
              </svg>
              {{ t("settings.export") }}
            </button>
            <button class="btn" @click="startImport">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
                <polyline points="7 10 12 15 17 10"></polyline>
                <line x1="12" y1="15" x2="12" y2="3"></line>
              </svg>
              {{ t("settings.import") }}
            </button>
          </div>
          <p class="backup-hint">{{ t("settings.backupHint") }}</p>
        </div>

        <div class="settings-section">
          <h4>{{ t("settings.about") }}</h4>
          <div class="about-info">
            <div class="app-name">{{ t("app.name") }}</div>
            <div class="app-version">{{ t("settings.version") }}: 0.1.0</div>
            <div class="app-desc">A simple open-source Termius alternative</div>
            <a class="app-github" href="https://github.com/dollarkillerx/MySSH" target="_blank">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/>
              </svg>
              GitHub
            </a>
          </div>
        </div>
      </div>
    </div>

    <!-- Password Modal -->
    <div v-if="showPasswordModal" class="password-overlay" @click.self="closePasswordModal">
      <div class="password-modal">
        <div class="password-header">
          <h4>{{ passwordModalMode === 'export' ? t("settings.exportTitle") : t("settings.importTitle") }}</h4>
          <button class="close-btn" @click="closePasswordModal">&times;</button>
        </div>
        <div class="password-body">
          <div class="form-group">
            <label>{{ t("settings.password") }}</label>
            <input v-model="password" type="password" :placeholder="t('settings.passwordPlaceholder')" />
          </div>
          <div v-if="passwordModalMode === 'export'" class="form-group">
            <label>{{ t("settings.confirmPassword") }}</label>
            <input v-model="confirmPassword" type="password" :placeholder="t('settings.confirmPasswordPlaceholder')" />
          </div>
        </div>
        <div class="password-footer">
          <button class="btn" @click="closePasswordModal">{{ t("common.cancel") }}</button>
          <button class="btn primary" @click="handlePasswordSubmit" :disabled="loading">
            {{ loading ? t("common.loading") : (passwordModalMode === 'export' ? t("settings.export") : t("settings.import")) }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.settings-modal {
  background: #1e1e2e;
  border-radius: 12px;
  width: 400px;
  max-height: 80vh;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  border-bottom: 1px solid #313244;
}

.modal-header h3 {
  margin: 0;
  color: #cdd6f4;
  font-size: 18px;
}

.close-btn {
  background: none;
  border: none;
  color: #6c7086;
  font-size: 24px;
  cursor: pointer;
  padding: 0;
  line-height: 1;
}

.close-btn:hover {
  color: #cdd6f4;
}

.modal-body {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
}

.settings-section {
  margin-bottom: 24px;
}

.settings-section:last-child {
  margin-bottom: 0;
}

.settings-section h4 {
  margin: 0 0 12px 0;
  color: #a6adc8;
  font-size: 14px;
  font-weight: 500;
}

.language-options {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.language-option {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  cursor: pointer;
  transition: background 0.15s;
}

.language-option:hover {
  background: #313244;
}

.language-option.active {
  background: #45475a;
}

.language-option input {
  display: none;
}

.option-label {
  color: #cdd6f4;
  font-size: 14px;
}

.option-hint {
  color: #6c7086;
  font-size: 12px;
}

.about-info {
  padding: 16px;
  background: #313244;
  border-radius: 8px;
}

.app-name {
  font-size: 18px;
  font-weight: 600;
  color: #cdd6f4;
  margin-bottom: 4px;
}

.app-version {
  font-size: 13px;
  color: #a6adc8;
  margin-bottom: 8px;
}

.app-desc {
  font-size: 13px;
  color: #6c7086;
}

.app-github {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 12px;
  padding: 8px 12px;
  background: #45475a;
  color: #cdd6f4;
  text-decoration: none;
  border-radius: 6px;
  font-size: 13px;
  transition: background 0.15s;
}

.app-github:hover {
  background: #585b70;
}

.backup-actions {
  display: flex;
  gap: 12px;
}

.backup-actions .btn {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 16px;
  background: #313244;
  color: #cdd6f4;
  border: none;
  border-radius: 8px;
  font-size: 14px;
  cursor: pointer;
}

.backup-actions .btn:hover {
  background: #45475a;
}

.backup-hint {
  margin-top: 12px;
  font-size: 12px;
  color: #6c7086;
}

.password-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1100;
}

.password-modal {
  background: #1e1e2e;
  border-radius: 12px;
  width: 360px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
}

.password-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  border-bottom: 1px solid #313244;
}

.password-header h4 {
  margin: 0;
  color: #cdd6f4;
  font-size: 16px;
}

.password-body {
  padding: 20px;
}

.form-group {
  margin-bottom: 16px;
}

.form-group:last-child {
  margin-bottom: 0;
}

.form-group label {
  display: block;
  margin-bottom: 6px;
  color: #a6adc8;
  font-size: 13px;
}

.form-group input {
  width: 100%;
  padding: 10px 12px;
  border-radius: 6px;
  border: 1px solid #313244;
  background: #313244;
  color: #cdd6f4;
  font-size: 14px;
  box-sizing: border-box;
}

.form-group input:focus {
  outline: none;
  border-color: #89b4fa;
}

.password-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 16px 20px;
  border-top: 1px solid #313244;
}

.btn {
  padding: 10px 16px;
  border-radius: 6px;
  border: none;
  font-size: 14px;
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
</style>
