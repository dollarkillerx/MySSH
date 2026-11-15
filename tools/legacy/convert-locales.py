#!/usr/bin/env python3
"""One-off: convert the Tauri-era vue-i18n JSON (tools/legacy/locales) into .resx for MySSH.Desktop,
merged with keys added in the .NET rewrite (NEW below). Re-runnable; overwrites the generated .resx files.
Placeholders keep the vue-i18n {name} form; MySSH.Desktop.Localization.Loc.Format() substitutes them."""
import json, os
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, 'tools', 'legacy', 'locales')
OUT = os.path.join(ROOT, 'src', 'MySSH.Desktop', 'Resources')
LANGS = {'en-US': '', 'zh-CN': '.zh-CN', 'ja-JP': '.ja-JP'}  # en-US is the neutral resource set

NEW = {
  'en-US': {
    'nav.hosts': 'Hosts', 'nav.identities': 'Keys & identities', 'nav.knownHosts': 'Known hosts',
    'nav.ai': 'AI / MCP', 'nav.settings': 'Settings', 'nav.expand': 'Expand navigation',
    'hosts.title': 'Hosts', 'hosts.add': 'New host', 'hosts.search': 'Search hosts…',
    'hosts.col.name': 'Name', 'hosts.col.address': 'Address', 'hosts.col.user': 'User',
    'hosts.col.auth': 'Auth', 'hosts.col.via': 'Via', 'hosts.col.ai': 'AI', 'hosts.count': '{count} hosts',
    'hosts.empty': 'No hosts yet', 'hosts.emptyHint': 'Add a host, or import from the previous version of MySSH.',
    'auth.password': 'Password', 'auth.key': 'Key', 'auth.none': '—',
    'aiPolicy.Off': 'Off', 'aiPolicy.ReadOnly': 'Read-only', 'aiPolicy.Ask': 'Ask', 'aiPolicy.Trusted': 'Trusted',
    'legacy.found.title': 'Data from the previous MySSH found',
    'legacy.found.body': '{count} hosts were found in the previous version. Import them? The old files are only read, never changed.',
    'legacy.import': 'Import', 'legacy.later': 'Not now',
    'legacy.done': 'Imported {imported} hosts ({identities} identities, {proxies} proxies).',
    'legacy.skipped': '{count} records could not be read and were skipped: {details}',
    'legacy.failed': 'Import failed: {error}', 'legacy.doneTitle': 'Import complete', 'legacy.skippedTitle': 'Some records were skipped',
    'vault.unlock.title': 'Unlock MySSH', 'vault.unlock.body': 'Enter your master password to decrypt saved credentials.',
    'vault.unlock.password': 'Master password', 'vault.unlock.action': 'Unlock', 'vault.unlock.wrong': 'Wrong master password.',
    'storage.openFailed': 'Could not open the MySSH data store: {error}',
    'storage.corrupt': '{count} saved records could not be decrypted. They are kept unchanged; see Settings › Data.',
  },
  'zh-CN': {
    'nav.hosts': '主机', 'nav.identities': '密钥与身份', 'nav.knownHosts': '已知主机',
    'nav.ai': 'AI / MCP', 'nav.settings': '设置', 'nav.expand': '展开导航',
    'hosts.title': '主机', 'hosts.add': '新建主机', 'hosts.search': '搜索主机…',
    'hosts.col.name': '名称', 'hosts.col.address': '地址', 'hosts.col.user': '用户',
    'hosts.col.auth': '认证', 'hosts.col.via': '经由', 'hosts.col.ai': 'AI', 'hosts.count': '{count} 台主机',
    'hosts.empty': '还没有主机', 'hosts.emptyHint': '新建一台主机，或从旧版 MySSH 导入。',
    'auth.password': '密码', 'auth.key': '密钥', 'auth.none': '—',
    'aiPolicy.Off': '关闭', 'aiPolicy.ReadOnly': '只读', 'aiPolicy.Ask': '询问', 'aiPolicy.Trusted': '信任',
    'legacy.found.title': '发现旧版 MySSH 的数据',
    'legacy.found.body': '在旧版中找到 {count} 台主机，是否导入？旧文件只会被读取，不会被修改。',
    'legacy.import': '导入', 'legacy.later': '暂不导入',
    'legacy.done': '已导入 {imported} 台主机（{identities} 个身份，{proxies} 个代理）。',
    'legacy.skipped': '有 {count} 条记录无法读取，已跳过：{details}',
    'legacy.failed': '导入失败：{error}', 'legacy.doneTitle': '导入完成', 'legacy.skippedTitle': '部分记录已跳过',
    'vault.unlock.title': '解锁 MySSH', 'vault.unlock.body': '输入主密码以解密已保存的凭据。',
    'vault.unlock.password': '主密码', 'vault.unlock.action': '解锁', 'vault.unlock.wrong': '主密码不正确。',
    'storage.openFailed': '无法打开 MySSH 数据存储：{error}',
    'storage.corrupt': '有 {count} 条已保存的记录无法解密，已原样保留；详见 设置 › 数据。',
  },
  'ja-JP': {
    'nav.hosts': 'ホスト', 'nav.identities': '鍵とID', 'nav.knownHosts': '既知のホスト',
    'nav.ai': 'AI / MCP', 'nav.settings': '設定', 'nav.expand': 'ナビゲーションを展開',
    'hosts.title': 'ホスト', 'hosts.add': '新規ホスト', 'hosts.search': 'ホストを検索…',
    'hosts.col.name': '名前', 'hosts.col.address': 'アドレス', 'hosts.col.user': 'ユーザー',
    'hosts.col.auth': '認証', 'hosts.col.via': '経由', 'hosts.col.ai': 'AI', 'hosts.count': '{count} 台のホスト',
    'hosts.empty': 'ホストがありません', 'hosts.emptyHint': 'ホストを追加するか、旧バージョンの MySSH からインポートしてください。',
    'auth.password': 'パスワード', 'auth.key': '鍵', 'auth.none': '—',
    'aiPolicy.Off': 'オフ', 'aiPolicy.ReadOnly': '読み取り専用', 'aiPolicy.Ask': '確認', 'aiPolicy.Trusted': '信頼',
    'legacy.found.title': '旧バージョンの MySSH のデータが見つかりました',
    'legacy.found.body': '旧バージョンに {count} 台のホストがあります。インポートしますか？旧ファイルは読み取るだけで変更しません。',
    'legacy.import': 'インポート', 'legacy.later': '後で',
    'legacy.done': '{imported} 台のホストをインポートしました（ID {identities} 件、プロキシ {proxies} 件）。',
    'legacy.skipped': '{count} 件のレコードを読み取れなかったためスキップしました：{details}',
    'legacy.failed': 'インポートに失敗しました：{error}', 'legacy.doneTitle': 'インポート完了', 'legacy.skippedTitle': '一部のレコードをスキップしました',
    'vault.unlock.title': 'MySSH のロック解除', 'vault.unlock.body': '保存済みの認証情報を復号するにはマスターパスワードを入力してください。',
    'vault.unlock.password': 'マスターパスワード', 'vault.unlock.action': 'ロック解除', 'vault.unlock.wrong': 'マスターパスワードが違います。',
    'storage.openFailed': 'MySSH のデータストアを開けませんでした：{error}',
    'storage.corrupt': '{count} 件の保存済みレコードを復号できませんでした。変更せずに保持しています。設定 › データ を確認してください。',
  },
}

def flat(o, p=''):
    for k, v in o.items():
        if isinstance(v, dict): yield from flat(v, p + k + '.')
        else: yield p + k, v

os.makedirs(OUT, exist_ok=True)
keysets = {}
for lang, suffix in LANGS.items():
    data = dict(flat(json.load(open(os.path.join(SRC, lang + '.json'), encoding='utf-8'))))
    data.update(NEW[lang])
    keysets[lang] = set(data)
    with open(os.path.join(OUT, f'Strings{suffix}.resx'), 'w', encoding='utf-8') as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n<!-- Generated by tools/legacy/convert-locales.py; edit there or edit this file and stop regenerating. -->\n<root>\n')
        f.write('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>\n')
        f.write('  <resheader name="version"><value>2.0</value></resheader>\n')
        f.write('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n')
        f.write('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n')
        for k in sorted(data):
            f.write(f'  <data name="{escape(k)}" xml:space="preserve"><value>{escape(str(data[k]))}</value></data>\n')
        f.write('</root>\n')
    print(lang, len(data), 'keys')
missing = {l: sorted(keysets['en-US'] - s) for l, s in keysets.items() if keysets['en-US'] - s}
print('missing vs en-US:', missing or 'none')
