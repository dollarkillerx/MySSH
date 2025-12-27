<script setup>
import { ref, onMounted } from "vue";
import { useI18n } from "vue-i18n";
import { getServers, deleteServer } from "../composables/useApi";

const { t } = useI18n();

const emit = defineEmits(["select", "edit", "add", "settings"]);

const servers = ref([]);
const loading = ref(false);

async function loadServers() {
  loading.value = true;
  try {
    servers.value = await getServers();
  } catch (error) {
    console.error("Failed to load servers:", error);
  } finally {
    loading.value = false;
  }
}

async function handleDelete(server, event) {
  event.stopPropagation();
  if (confirm(t("servers.deleteConfirm", { name: server.name }))) {
    try {
      await deleteServer(server.id);
      await loadServers();
    } catch (error) {
      alert(t("serverForm.deleteFailed", { error }));
    }
  }
}

function handleEdit(server, event) {
  event.stopPropagation();
  emit("edit", server);
}

onMounted(loadServers);

defineExpose({ loadServers });
</script>

<template>
  <div class="server-list">
    <div class="header">
      <h2>{{ t("servers.title") }}</h2>
      <div class="header-actions">
        <button class="icon-btn" @click="$emit('settings')" :title="t('settings.title')">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="12" cy="12" r="3"></circle>
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"></path>
          </svg>
        </button>
        <button class="add-btn" @click="$emit('add')" :title="t('servers.add')">+</button>
      </div>
    </div>

    <div v-if="loading" class="loading">{{ t("servers.loading") }}</div>

    <div v-else-if="servers.length === 0" class="empty">
      {{ t("servers.empty") }}<br />
      {{ t("servers.emptyHint") }}
    </div>

    <div v-else class="list">
      <div
        v-for="server in servers"
        :key="server.id"
        class="server-item"
        @click="$emit('select', server)"
      >
        <div class="server-info">
          <div class="server-name">{{ server.name }}</div>
          <div class="server-host">{{ server.username }}@{{ server.host }}:{{ server.port }}</div>
          <div class="server-tags">
            <span class="tag" :class="server.auth_type">{{ server.auth_type }}</span>
            <span v-if="server.has_proxy" class="tag proxy">proxy</span>
          </div>
        </div>
        <div class="server-actions">
          <button class="action-btn" @click="handleEdit(server, $event)" :title="t('common.edit')">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"></path>
              <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"></path>
            </svg>
          </button>
          <button class="action-btn delete" @click="handleDelete(server, $event)" :title="t('common.delete')">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="3 6 5 6 21 6"></polyline>
              <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
            </svg>
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.server-list {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #1e1e2e;
  border-right: 1px solid #313244;
}

.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px;
  border-bottom: 1px solid #313244;
}

.header h2 {
  margin: 0;
  font-size: 16px;
  color: #cdd6f4;
}

.header-actions {
  display: flex;
  gap: 8px;
}

.icon-btn {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: #6c7086;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
}

.icon-btn:hover {
  background: #313244;
  color: #cdd6f4;
}

.add-btn {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: #89b4fa;
  color: #1e1e2e;
  font-size: 18px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
}

.add-btn:hover {
  background: #b4befe;
}

.loading,
.empty {
  padding: 20px;
  text-align: center;
  color: #6c7086;
  font-size: 14px;
}

.list {
  flex: 1;
  overflow-y: auto;
  padding: 8px;
}

.server-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px;
  margin-bottom: 4px;
  border-radius: 8px;
  background: #313244;
  cursor: pointer;
  transition: background 0.2s;
}

.server-item:hover {
  background: #45475a;
}

.server-info {
  flex: 1;
  min-width: 0;
}

.server-name {
  font-weight: 500;
  color: #cdd6f4;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.server-host {
  font-size: 12px;
  color: #6c7086;
  margin-top: 2px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.server-tags {
  display: flex;
  gap: 4px;
  margin-top: 6px;
}

.tag {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 4px;
  background: #45475a;
  color: #a6adc8;
}

.tag.password {
  background: #fab387;
  color: #1e1e2e;
}

.tag.key {
  background: #a6e3a1;
  color: #1e1e2e;
}

.tag.proxy {
  background: #89b4fa;
  color: #1e1e2e;
}

.server-actions {
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.2s;
}

.server-item:hover .server-actions {
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

.action-btn:hover {
  background: #585b70;
  color: #cdd6f4;
}

.action-btn.delete:hover {
  background: #f38ba8;
  color: #1e1e2e;
}
</style>
