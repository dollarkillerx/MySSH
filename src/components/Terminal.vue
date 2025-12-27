<script setup>
import { ref, onMounted, onUnmounted, watch, nextTick } from "vue";
import { useI18n } from "vue-i18n";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { sshConnect, sshWrite, sshResize, sshDisconnect, onSshData } from "../composables/useApi";

const { t } = useI18n();

const props = defineProps({
  server: Object,
  active: Boolean,
});

const emit = defineEmits(["close"]);

const terminalRef = ref(null);
const status = ref("connecting");
const errorMessage = ref("");

let terminal = null;
let fitAddon = null;
let sessionId = null;
let unlistenData = null;
let resizeObserver = null;
let lastCols = 0;
let lastRows = 0;

async function connect() {
  if (!props.server) return;

  status.value = "connecting";
  errorMessage.value = "";

  try {
    terminal = new Terminal({
      cursorBlink: true,
      fontSize: 14,
      fontFamily: 'Menlo, Monaco, "Courier New", monospace',
      theme: {
        background: "#1e1e2e",
        foreground: "#cdd6f4",
        cursor: "#f5e0dc",
        cursorAccent: "#1e1e2e",
        selectionBackground: "#45475a",
        black: "#45475a",
        red: "#f38ba8",
        green: "#a6e3a1",
        yellow: "#f9e2af",
        blue: "#89b4fa",
        magenta: "#f5c2e7",
        cyan: "#94e2d5",
        white: "#bac2de",
        brightBlack: "#585b70",
        brightRed: "#f38ba8",
        brightGreen: "#a6e3a1",
        brightYellow: "#f9e2af",
        brightBlue: "#89b4fa",
        brightMagenta: "#f5c2e7",
        brightCyan: "#94e2d5",
        brightWhite: "#a6adc8",
      },
    });

    fitAddon = new FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.loadAddon(new WebLinksAddon());

    await nextTick();
    terminal.open(terminalRef.value);
    fitAddon.fit();

    const cols = terminal.cols;
    const rows = terminal.rows;
    sessionId = await sshConnect(props.server.id, cols, rows);

    unlistenData = await onSshData(sessionId, (data) => {
      terminal.write(data);
    });

    terminal.onData((data) => {
      if (sessionId) {
        const encoder = new TextEncoder();
        sshWrite(sessionId, encoder.encode(data));
      }
    });

    resizeObserver = new ResizeObserver(() => {
      if (fitAddon && terminal && sessionId && terminalRef.value) {
        // Only resize if the terminal is visible (has non-zero dimensions)
        const rect = terminalRef.value.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0) {
          fitAddon.fit();
          // Only send resize if dimensions actually changed
          if (terminal.cols !== lastCols || terminal.rows !== lastRows) {
            lastCols = terminal.cols;
            lastRows = terminal.rows;
            sshResize(sessionId, terminal.cols, terminal.rows);
          }
        }
      }
    });
    resizeObserver.observe(terminalRef.value);

    // Store initial dimensions
    lastCols = terminal.cols;
    lastRows = terminal.rows;

    status.value = "connected";
    terminal.focus();
  } catch (error) {
    status.value = "error";
    errorMessage.value = error.toString();
  }
}

async function disconnect() {
  if (sessionId) {
    await sshDisconnect(sessionId);
    sessionId = null;
  }
  if (unlistenData) {
    unlistenData();
    unlistenData = null;
  }
  if (resizeObserver) {
    resizeObserver.disconnect();
    resizeObserver = null;
  }
  if (terminal) {
    terminal.dispose();
    terminal = null;
  }
}

async function reconnect() {
  await disconnect();
  await connect();
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

// Handle tab becoming active
watch(
  () => props.active,
  (isActive) => {
    if (isActive && terminal && fitAddon && status.value === "connected") {
      nextTick(() => {
        fitAddon.fit();
        terminal.focus();
      });
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
  <div class="terminal-container">
    <div class="terminal-header">
      <div class="terminal-title">
        <span class="status-dot" :class="status"></span>
        {{ server?.name || "Terminal" }}
        <span class="server-info">{{ server?.username }}@{{ server?.host }}:{{ server?.port }}</span>
      </div>
      <div class="terminal-actions">
        <button v-if="status === 'connected'" class="action-btn" @click="reconnect" :title="t('terminal.reconnect')">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="23 4 23 10 17 10"></polyline>
            <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"></path>
          </svg>
        </button>
        <button class="action-btn close" @click="$emit('close')" :title="t('terminal.close')">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <line x1="18" y1="6" x2="6" y2="18"></line>
            <line x1="6" y1="6" x2="18" y2="18"></line>
          </svg>
        </button>
      </div>
    </div>

    <div class="terminal-body">
      <div v-if="status === 'connecting'" class="terminal-status">
        <div class="spinner"></div>
        <span>{{ t("terminal.connecting", { host: server?.host }) }}</span>
      </div>

      <div v-else-if="status === 'error'" class="terminal-status error">
        <span>{{ t("terminal.connectionFailed", { error: errorMessage }) }}</span>
        <button class="btn" @click="reconnect">{{ t("terminal.retry") }}</button>
      </div>

      <div ref="terminalRef" class="terminal-xterm" :class="{ hidden: status !== 'connected' }"></div>
    </div>
  </div>
</template>

<style>
@import "@xterm/xterm/css/xterm.css";
</style>

<style scoped>
.terminal-container {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #1e1e2e;
}

.terminal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 16px;
  background: #181825;
  border-bottom: 1px solid #313244;
}

.terminal-title {
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

.server-info {
  color: #6c7086;
  font-weight: normal;
  font-size: 12px;
}

.terminal-actions {
  display: flex;
  gap: 4px;
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
  background: #313244;
  color: #cdd6f4;
}

.action-btn.close:hover {
  background: #f38ba8;
  color: #1e1e2e;
}

.terminal-body {
  flex: 1;
  position: relative;
  overflow: hidden;
}

.terminal-status {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
  color: #a6adc8;
  text-align: center;
  max-width: 80%;
}

.terminal-status.error {
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

.btn:hover {
  background: #b4befe;
}

.terminal-xterm {
  width: 100%;
  height: 100%;
  padding: 8px;
  box-sizing: border-box;
}

.terminal-xterm.hidden {
  visibility: hidden;
}

.terminal-xterm :deep(.xterm) {
  height: 100%;
}

.terminal-xterm :deep(.xterm-viewport) {
  overflow-y: auto !important;
}
</style>
