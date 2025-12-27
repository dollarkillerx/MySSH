<script setup>
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import ServerList from "./components/ServerList.vue";
import ServerForm from "./components/ServerForm.vue";
import Terminal from "./components/Terminal.vue";
import FileBrowser from "./components/FileBrowser.vue";
import Settings from "./components/Settings.vue";

const { t } = useI18n();

const serverListRef = ref(null);
const showForm = ref(false);
const showSettings = ref(false);
const editServerId = ref(null);

const activeTab = ref(null); // { type: 'terminal' | 'sftp', server }
const tabs = ref([]);

function handleAddServer() {
  editServerId.value = null;
  showForm.value = true;
}

function handleEditServer(server) {
  editServerId.value = server.id;
  showForm.value = true;
}

function handleServerSaved() {
  serverListRef.value?.loadServers();
}

function handleSelectServer(server) {
  openTerminal(server);
}

function openTerminal(server) {
  const existingTab = tabs.value.find(
    (t) => t.type === "terminal" && t.server.id === server.id
  );
  if (existingTab) {
    activeTab.value = existingTab;
  } else {
    const newTab = { type: "terminal", server, id: `terminal-${server.id}-${Date.now()}` };
    tabs.value.push(newTab);
    activeTab.value = newTab;
  }
}

function openSftp(server) {
  const existingTab = tabs.value.find(
    (t) => t.type === "sftp" && t.server.id === server.id
  );
  if (existingTab) {
    activeTab.value = existingTab;
  } else {
    const newTab = { type: "sftp", server, id: `sftp-${server.id}-${Date.now()}` };
    tabs.value.push(newTab);
    activeTab.value = newTab;
  }
}

function closeTab(tab) {
  const index = tabs.value.indexOf(tab);
  if (index > -1) {
    tabs.value.splice(index, 1);
    if (activeTab.value === tab) {
      activeTab.value = tabs.value[Math.max(0, index - 1)] || null;
    }
  }
}

function getTabTitle(tab) {
  const prefix = tab.type === "terminal" ? "SSH" : "SFTP";
  return `${prefix}: ${tab.server.name}`;
}
</script>

<template>
  <div class="app">
    <aside class="sidebar">
      <ServerList
        ref="serverListRef"
        @select="handleSelectServer"
        @edit="handleEditServer"
        @add="handleAddServer"
        @settings="showSettings = true"
      />
    </aside>

    <main class="main">
      <div v-if="tabs.length === 0" class="welcome">
        <div class="welcome-content">
          <img src="/logo.png" alt="MySSH" class="welcome-logo" />
          <h1>{{ t("app.name") }}</h1>
          <p>{{ t("app.welcome") }}</p>
        </div>
      </div>

      <template v-else>
        <div class="tabs">
          <div
            v-for="tab in tabs"
            :key="tab.id"
            class="tab"
            :class="{ active: activeTab === tab }"
            @click="activeTab = tab"
          >
            <span class="tab-icon">
              <svg v-if="tab.type === 'terminal'" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="4 17 10 11 4 5"></polyline>
                <line x1="12" y1="19" x2="20" y2="19"></line>
              </svg>
              <svg v-else width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path>
              </svg>
            </span>
            <span class="tab-title">{{ getTabTitle(tab) }}</span>
            <button class="tab-close" @click.stop="closeTab(tab)">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <line x1="18" y1="6" x2="6" y2="18"></line>
                <line x1="6" y1="6" x2="18" y2="18"></line>
              </svg>
            </button>
          </div>
          <div class="tabs-actions" v-if="activeTab">
            <button
              v-if="activeTab.type === 'terminal'"
              class="tab-action"
              @click="openSftp(activeTab.server)"
              :title="t('app.openSftp')"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path>
              </svg>
            </button>
            <button
              v-else
              class="tab-action"
              @click="openTerminal(activeTab.server)"
              :title="t('app.openTerminal')"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <polyline points="4 17 10 11 4 5"></polyline>
                <line x1="12" y1="19" x2="20" y2="19"></line>
              </svg>
            </button>
          </div>
        </div>

        <div class="tab-content">
          <template v-for="tab in tabs" :key="tab.id">
            <div v-show="activeTab === tab" class="tab-panel">
              <Terminal
                v-if="tab.type === 'terminal'"
                :server="tab.server"
                @close="closeTab(tab)"
              />
              <FileBrowser
                v-else
                :server="tab.server"
                @close="closeTab(tab)"
              />
            </div>
          </template>
        </div>
      </template>
    </main>

    <ServerForm
      :visible="showForm"
      :server-id="editServerId"
      @close="showForm = false"
      @saved="handleServerSaved"
    />

    <Settings
      :visible="showSettings"
      @close="showSettings = false"
      @refresh="serverListRef?.loadServers()"
    />
  </div>
</template>

<style>
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

html,
body,
#app {
  height: 100%;
  overflow: hidden;
}

body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Oxygen,
    Ubuntu, Cantarell, "Open Sans", "Helvetica Neue", sans-serif;
  background: #1e1e2e;
  color: #cdd6f4;
}
</style>

<style scoped>
.app {
  display: flex;
  height: 100%;
}

.sidebar {
  width: 280px;
  flex-shrink: 0;
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.welcome {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
}

.welcome-content {
  text-align: center;
  color: #6c7086;
}

.welcome-logo {
  width: 96px;
  height: auto;
  margin-bottom: 16px;
}

.welcome-content h1 {
  font-size: 32px;
  font-weight: 600;
  margin-bottom: 8px;
  color: #cdd6f4;
}

.welcome-content p {
  font-size: 14px;
}

.tabs {
  display: flex;
  align-items: center;
  background: #181825;
  border-bottom: 1px solid #313244;
  padding: 0 8px;
  height: 40px;
  overflow-x: auto;
}

.tab {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 6px 6px 0 0;
  cursor: pointer;
  color: #6c7086;
  font-size: 13px;
  white-space: nowrap;
  transition: background 0.15s, color 0.15s;
}

.tab:hover {
  background: #313244;
  color: #a6adc8;
}

.tab.active {
  background: #1e1e2e;
  color: #cdd6f4;
}

.tab-icon {
  display: flex;
  align-items: center;
}

.tab-title {
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.tab-close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  border: none;
  background: transparent;
  color: inherit;
  border-radius: 4px;
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s, background 0.15s;
}

.tab:hover .tab-close {
  opacity: 1;
}

.tab-close:hover {
  background: #45475a;
}

.tabs-actions {
  margin-left: auto;
  padding-left: 8px;
}

.tab-action {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: none;
  background: transparent;
  color: #6c7086;
  border-radius: 6px;
  cursor: pointer;
}

.tab-action:hover {
  background: #313244;
  color: #cdd6f4;
}

.tab-content {
  flex: 1;
  position: relative;
  overflow: hidden;
}

.tab-panel {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
}
</style>
