import { createI18n } from 'vue-i18n';
import enUS from './locales/en-US.json';
import zhCN from './locales/zh-CN.json';
import jaJP from './locales/ja-JP.json';

export const SUPPORTED_LOCALES = [
  { code: 'en-US', name: 'English' },
  { code: 'zh-CN', name: '简体中文' },
  { code: 'ja-JP', name: '日本語' },
];

const STORAGE_KEY = 'myssh-locale';

function getSystemLocale() {
  const lang = navigator.language || navigator.userLanguage || 'en-US';

  // Exact match
  const exact = SUPPORTED_LOCALES.find(l => l.code === lang);
  if (exact) return exact.code;

  // Language prefix match (e.g., "zh" matches "zh-CN")
  const prefix = lang.split('-')[0];
  const prefixMatch = SUPPORTED_LOCALES.find(l => l.code.startsWith(prefix));
  if (prefixMatch) return prefixMatch.code;

  return 'en-US';
}

function getSavedLocale() {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

export function saveLocale(locale) {
  try {
    if (locale === 'auto') {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, locale);
    }
  } catch {
    // Ignore storage errors
  }
}

export function getInitialLocale() {
  const saved = getSavedLocale();
  if (saved && SUPPORTED_LOCALES.some(l => l.code === saved)) {
    return saved;
  }
  return getSystemLocale();
}

export function isAutoLocale() {
  return !getSavedLocale();
}

const i18n = createI18n({
  legacy: false,
  locale: getInitialLocale(),
  fallbackLocale: 'en-US',
  messages: {
    'en-US': enUS,
    'zh-CN': zhCN,
    'ja-JP': jaJP,
  },
});

export default i18n;
