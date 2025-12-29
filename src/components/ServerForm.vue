<script setup>
import { ref, watch, computed } from "vue";
import { useI18n } from "vue-i18n";
import { saveServer, getServer, getServers } from "../composables/useApi";

const { t } = useI18n();

const props = defineProps({
  serverId: String,
  visible: Boolean,
});

const emit = defineEmits(["close", "saved"]);

const loading = ref(false);
const form = ref(getDefaultForm());
const allServers = ref([]);

// Password visibility toggles
const showPassword = ref(false);
const showPassphrase = ref(false);
const showProxyPassword = ref(false);

function getDefaultForm() {
  return {
    id: null,
    name: "",
    host: "",
    port: 22,
    username: "root",
    auth_type: "password",
    password: "",
    private_key: "",
    passphrase: "",
    proxy_enabled: false,
    proxy_type: "socks5",
    proxy_host: "",
    proxy_port: 1080,
    proxy_username: "",
    proxy_password: "",
    jump_host: "",
    notes: "",
  };
}

// Get available servers for jump host selection (exclude current server)
const availableJumpHosts = computed(() => {
  return allServers.value.filter(s => s.id !== form.value.id);
});

// Load all servers for jump host dropdown
async function loadServers() {
  try {
    allServers.value = await getServers();
  } catch (error) {
    console.error("Failed to load servers:", error);
  }
}

const isEdit = computed(() => !!form.value.id);

watch(
  () => props.serverId,
  async (id) => {
    // Load servers list for jump host selection
    await loadServers();

    if (id) {
      try {
        const server = await getServer(id);
        if (server) {
          form.value = {
            id: server.id,
            name: server.name,
            host: server.host,
            port: server.port,
            username: server.username,
            auth_type: server.auth.type === "PrivateKey" ? "key" : "password",
            password: server.auth.type === "Password" ? server.auth.value : "",
            private_key: server.auth.type === "PrivateKey" ? server.auth.value.key : "",
            passphrase: server.auth.type === "PrivateKey" ? server.auth.value.passphrase || "" : "",
            proxy_enabled: !!server.proxy,
            proxy_type: server.proxy?.proxy_type || "socks5",
            proxy_host: server.proxy?.host || "",
            proxy_port: server.proxy?.port || 1080,
            proxy_username: server.proxy?.username || "",
            proxy_password: server.proxy?.password || "",
            jump_host: server.jump_host || "",
            notes: server.notes || "",
          };
        }
      } catch (error) {
        console.error("Failed to load server:", error);
      }
    } else {
      form.value = getDefaultForm();
    }
  },
  { immediate: true }
);

watch(
  () => props.visible,
  (visible) => {
    if (visible && !props.serverId) {
      form.value = getDefaultForm();
    }
    if (!visible) {
      // Reset password visibility when modal closes
      showPassword.value = false;
      showPassphrase.value = false;
      showProxyPassword.value = false;
    }
  }
);

async function handleSubmit() {
  if (!form.value.name || !form.value.host || !form.value.username) {
    alert(t("serverForm.required"));
    return;
  }

  if (form.value.auth_type === "password" && !form.value.password) {
    alert(t("serverForm.passwordRequired"));
    return;
  }

  if (form.value.auth_type === "key" && !form.value.private_key) {
    alert(t("serverForm.privateKeyRequired"));
    return;
  }

  loading.value = true;
  try {
    await saveServer(form.value);
    emit("saved");
    emit("close");
  } catch (error) {
    alert(t("serverForm.saveFailed", { error }));
  } finally {
    loading.value = false;
  }
}

function handleFileSelect(event) {
  const file = event.target.files[0];
  if (file) {
    const reader = new FileReader();
    reader.onload = (e) => {
      form.value.private_key = e.target.result;
    };
    reader.readAsText(file);
  }
}
</script>

<template>
  <div v-if="visible" class="modal-overlay" @click.self="$emit('close')">
    <div class="modal">
      <div class="modal-header">
        <h3>{{ isEdit ? t("servers.edit") : t("servers.add") }}</h3>
        <button class="close-btn" @click="$emit('close')">&times;</button>
      </div>

      <form class="modal-body" @submit.prevent="handleSubmit">
        <div class="form-section">
          <h4>{{ t("serverForm.basicInfo") }}</h4>

          <div class="form-group">
            <label>{{ t("serverForm.name") }} *</label>
            <input v-model="form.name" type="text" :placeholder="t('serverForm.namePlaceholder')" required />
          </div>

          <div class="form-row">
            <div class="form-group flex-1">
              <label>{{ t("serverForm.host") }} *</label>
              <input v-model="form.host" type="text" :placeholder="t('serverForm.hostPlaceholder')" required />
            </div>
            <div class="form-group" style="width: 100px">
              <label>{{ t("serverForm.port") }}</label>
              <input v-model.number="form.port" type="number" min="1" max="65535" />
            </div>
          </div>

          <div class="form-group">
            <label>{{ t("serverForm.username") }} *</label>
            <input v-model="form.username" type="text" :placeholder="t('serverForm.usernamePlaceholder')" required />
          </div>
        </div>

        <div class="form-section">
          <h4>{{ t("serverForm.authentication") }}</h4>

          <div class="form-group">
            <label>{{ t("serverForm.authType") }}</label>
            <div class="radio-group">
              <label class="radio">
                <input v-model="form.auth_type" type="radio" value="password" />
                <span>{{ t("serverForm.password") }}</span>
              </label>
              <label class="radio">
                <input v-model="form.auth_type" type="radio" value="key" />
                <span>{{ t("serverForm.privateKey") }}</span>
              </label>
            </div>
          </div>

          <div v-if="form.auth_type === 'password'" class="form-group">
            <label>{{ t("serverForm.password") }} *</label>
            <div class="password-input">
              <input
                v-model="form.password"
                :type="showPassword ? 'text' : 'password'"
                :placeholder="t('serverForm.passwordPlaceholder')"
              />
              <button type="button" class="toggle-password" @click="showPassword = !showPassword">
                <svg v-if="showPassword" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"></path>
                  <line x1="1" y1="1" x2="23" y2="23"></line>
                </svg>
                <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path>
                  <circle cx="12" cy="12" r="3"></circle>
                </svg>
              </button>
            </div>
          </div>

          <template v-else>
            <div class="form-group">
              <label>{{ t("serverForm.privateKey") }} *</label>
              <div class="file-input">
                <input type="file" @change="handleFileSelect" accept=".pem,.key,.pub,*" />
                <span>{{ form.private_key ? t("serverForm.privateKeyLoaded") : t("serverForm.privateKeyHint") }}</span>
              </div>
              <textarea
                v-model="form.private_key"
                :placeholder="t('serverForm.privateKeyPaste')"
                rows="4"
              ></textarea>
            </div>

            <div class="form-group">
              <label>{{ t("serverForm.passphrase") }}</label>
              <div class="password-input">
                <input
                  v-model="form.passphrase"
                  :type="showPassphrase ? 'text' : 'password'"
                  :placeholder="t('serverForm.passphrasePlaceholder')"
                />
                <button type="button" class="toggle-password" @click="showPassphrase = !showPassphrase">
                  <svg v-if="showPassphrase" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"></path>
                    <line x1="1" y1="1" x2="23" y2="23"></line>
                  </svg>
                  <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path>
                    <circle cx="12" cy="12" r="3"></circle>
                  </svg>
                </button>
              </div>
            </div>
          </template>
        </div>

        <div class="form-section">
          <h4>
            <label class="checkbox">
              <input v-model="form.proxy_enabled" type="checkbox" />
              <span>{{ t("serverForm.useProxy") }}</span>
            </label>
          </h4>

          <template v-if="form.proxy_enabled">
            <div class="form-group">
              <label>{{ t("serverForm.proxyType") }}</label>
              <div class="radio-group">
                <label class="radio">
                  <input v-model="form.proxy_type" type="radio" value="socks5" />
                  <span>SOCKS5</span>
                </label>
                <label class="radio">
                  <input v-model="form.proxy_type" type="radio" value="http" />
                  <span>HTTP</span>
                </label>
              </div>
            </div>

            <div class="form-row">
              <div class="form-group flex-1">
                <label>{{ t("serverForm.proxyHost") }} *</label>
                <input v-model="form.proxy_host" type="text" placeholder="127.0.0.1" />
              </div>
              <div class="form-group" style="width: 100px">
                <label>{{ t("serverForm.proxyPort") }}</label>
                <input v-model.number="form.proxy_port" type="number" min="1" max="65535" />
              </div>
            </div>

            <div class="form-row">
              <div class="form-group flex-1">
                <label>{{ t("serverForm.proxyUsername") }}</label>
                <input v-model="form.proxy_username" type="text" />
              </div>
              <div class="form-group flex-1">
                <label>{{ t("serverForm.proxyPassword") }}</label>
                <div class="password-input">
                  <input
                    v-model="form.proxy_password"
                    :type="showProxyPassword ? 'text' : 'password'"
                  />
                  <button type="button" class="toggle-password" @click="showProxyPassword = !showProxyPassword">
                    <svg v-if="showProxyPassword" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                      <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"></path>
                      <line x1="1" y1="1" x2="23" y2="23"></line>
                    </svg>
                    <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path>
                      <circle cx="12" cy="12" r="3"></circle>
                    </svg>
                  </button>
                </div>
              </div>
            </div>
          </template>
        </div>

        <div class="form-section">
          <h4>{{ t("serverForm.jumpHost") }}</h4>
          <div class="form-group">
            <label>{{ t("serverForm.jumpHostSelect") }}</label>
            <select v-model="form.jump_host" class="select-input">
              <option value="">{{ t("serverForm.jumpHostNone") }}</option>
              <option v-for="server in availableJumpHosts" :key="server.id" :value="server.id">
                {{ server.name }} ({{ server.host }})
              </option>
            </select>
            <span class="help-text">{{ t("serverForm.jumpHostHint") }}</span>
          </div>
        </div>

        <div class="form-section">
          <h4>{{ t("serverForm.notes") }}</h4>
          <div class="form-group">
            <textarea v-model="form.notes" :placeholder="t('serverForm.notesPlaceholder')" rows="3"></textarea>
          </div>
        </div>

        <div class="modal-footer">
          <button type="button" class="btn secondary" @click="$emit('close')">{{ t("serverForm.cancel") }}</button>
          <button type="submit" class="btn primary" :disabled="loading">
            {{ loading ? t("serverForm.saving") : t("serverForm.save") }}
          </button>
        </div>
      </form>
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

.modal {
  background: #1e1e2e;
  border-radius: 12px;
  width: 500px;
  max-height: 90vh;
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

.form-section {
  margin-bottom: 24px;
}

.form-section h4 {
  margin: 0 0 12px 0;
  color: #a6adc8;
  font-size: 14px;
  font-weight: 500;
}

.form-group {
  margin-bottom: 12px;
}

.form-group label {
  display: block;
  margin-bottom: 4px;
  color: #6c7086;
  font-size: 12px;
}

.form-row {
  display: flex;
  gap: 12px;
}

.flex-1 {
  flex: 1;
}

input[type="text"],
input[type="password"],
input[type="number"],
textarea,
select {
  width: 100%;
  padding: 10px 12px;
  border-radius: 6px;
  border: 1px solid #313244;
  background: #313244;
  color: #cdd6f4;
  font-size: 14px;
  box-sizing: border-box;
}

.select-input {
  appearance: none;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 24 24' fill='none' stroke='%236c7086' stroke-width='2'%3E%3Cpath d='M6 9l6 6 6-6'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right 12px center;
  padding-right: 36px;
  cursor: pointer;
}

.select-input:focus {
  outline: none;
  border-color: #89b4fa;
}

.help-text {
  display: block;
  margin-top: 4px;
  color: #6c7086;
  font-size: 12px;
}

input:focus,
textarea:focus {
  outline: none;
  border-color: #89b4fa;
}

.password-input {
  position: relative;
  display: flex;
  align-items: center;
}

.password-input input {
  padding-right: 40px;
}

.toggle-password {
  position: absolute;
  right: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: none;
  background: transparent;
  color: #6c7086;
  cursor: pointer;
  border-radius: 4px;
}

.toggle-password:hover {
  background: #45475a;
  color: #cdd6f4;
}

textarea {
  resize: vertical;
  font-family: monospace;
}

.radio-group {
  display: flex;
  gap: 16px;
}

.radio,
.checkbox {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  color: #cdd6f4;
  font-size: 14px;
}

.radio input,
.checkbox input {
  width: auto;
  margin: 0;
}

.file-input {
  position: relative;
  margin-bottom: 8px;
}

.file-input input {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: pointer;
}

.file-input span {
  display: block;
  padding: 10px 12px;
  border-radius: 6px;
  border: 1px dashed #45475a;
  background: #313244;
  color: #6c7086;
  font-size: 14px;
  text-align: center;
}

.modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 16px 20px;
  border-top: 1px solid #313244;
}

.btn {
  padding: 10px 20px;
  border-radius: 6px;
  border: none;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.2s;
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

.btn.secondary {
  background: #45475a;
  color: #cdd6f4;
}

.btn.secondary:hover {
  background: #585b70;
}
</style>
