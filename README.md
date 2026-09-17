# VpnUs — VPN-клиент для Windows 11 на ядре sing-box

Клиент с главной фичей **«VPN только для выбранных приложений»** (per-app split tunneling),
поддержкой Happ/v2rayNG-совместимых подписок, TUN-режимом через службу Windows
(интерфейс не требует прав администратора) и обновлением ядра/приложения из GitHub.

> Только для Windows 10/11 (x64 и arm64). Ядро — официальный [sing-box](https://github.com/SagerNet/sing-box),
> никакой телеметрии: подписка, настройки и логи не покидают машину.

## Возможности

- **Per-app split tunneling**: выбранные приложения идут через VPN, остальные — напрямую
  (sing-box `process_name` / `process_path` / `process_path_regex` + `route.find_process`).
  3 режима: *только выбранные*, *все приложения*, *все, кроме выбранных*.
- **Режимы маршрутизации**: только выбранные приложения, только заблокированное в РФ (Re-filter lists),
  только выбранные ресурсы (geosite), всё кроме РФ, Global.
- **Подписки**: URL, `User-Agent: v2rayNG/1.8.5`, cookie-jar и повторный запрос при `307` + `Set-Cookie`
  (как в Happ), base64/plain тело, автообновление раз в N часов.
  Схемы: `vless` (reality/utls/tcp+vision/ws/grpc/http/h2/quic/httpupgrade), `vmess`, `trojan`,
  `ss` (SIP002 и legacy), `hysteria2`/`hy2`, `tuic`, `anytls`. Неподдерживаемые транспорты пропускаются.
- **Серверы**: список с флагами/типом/адресом, выбор, Auto (urltest), «Test all» через Clash API.
- **Приложения**: сканирование ярлыков «Пуск», Uninstall-реестра, пакетов Store и запущенных процессов,
  поиск/фильтры, «Добавить .exe», иконки.
- **Игры напрямую**: `geosite-category-games` + игровые порты (27000-27100, 34715 и т.д.).
- **Служба Windows**: sing-box работает от `LocalSystem`, UI общается с ним через named pipe
  с токеном и списком SID (`ipc.allow`). UI запускается без UAC.
- **Обновления**: ядро sing-box + wintun — кнопкой; обновления приложения — проверка GitHub Releases
  и установка прямо из UI.
- **Трей**, тёмная тема, Mica, логи с экспортом, `run at login`.

## Установка

### Установщик (рекомендуется)

1. Скачайте `VpnUs-Setup-<версия>-x64.exe` со страницы [Releases](https://github.com/Darber155/vpnus_desktop/releases).
2. Запустите (потребуются права администратора — устанавливается служба).
3. В приложении: **Настройки → «Обновить ядро»** (скачает sing-box и wintun),
   затем укажите **URL подписки** и нажмите «Обновить сейчас».
4. Выберите режим на экране **«Приложения»** и нажмите «Применить».

### Портативная версия

1. Распакуйте `VpnUs-portable-<версия>-x64.zip` в любую папку (не в Program Files).
2. Запустите `VpnUs.exe` — все данные будут в `data\` рядом с приложением (`portable.flag`).
3. Для TUN: **Настройки → «Установить / починить службу (UAC)»** (служба берётся из папки `service`).

## Сборка из исходников

Требования: .NET SDK 8+ и Windows.

```powershell
git clone https://github.com/Darber155/vpnus_desktop.git
cd vpnus_desktop
dotnet build -c Release
dotnet test  tests\VpnUs.Core.Tests\VpnUs.Core.Tests.csproj
```

Запуск из сборки: `src\VpnUs.App\bin\Release\net8.0-windows\VpnUs.exe`
(рядом появится `service\VpnUs.Service.exe` — он нужен для установки службы из UI).

### Дистрибутивы

```powershell
# портативная версия + установщик для x64
powershell -File build\build-packages.ps1 -Version 1.0.0 -Runtimes win-x64

# обе архитектуры
powershell -File build\build-packages.ps1 -Version 1.0.0 -Runtimes win-x64,win-arm64
```

Результат в `dist\`:

| Файл | Что это |
|---|---|
| `VpnUs-portable-<ver>-<arch>.zip` | портативная сборка (self-contained) |
| `VpnUs-Setup-<ver>-<arch>.exe` | установщик Inno Setup (self-contained, ставит службу) |

Установщик собирается [Inno Setup 6](https://jrsoftware.org/isinfo.php) — скрипт `build\build-packages.ps1`
сам поставит его через `winget`, если компилятор не найден.
Иконка генерируется скриптом `build\make-icon.ps1` в `installer\vpnus.ico`.

## Архитектура

```
src/VpnUs.Core      — модели, парсер подписок, генератор config.json, Clash API, сканер приложений, IPC-контракты
src/VpnUs.Service   — служба: supervisor sing-box, named pipe + токен, планировщик подписки, обновление ядра
src/VpnUs.App       — WPF UI (MVVM, CommunityToolkit.Mvvm): Главная, Серверы, Приложения, Настройки, Логи, трей
tests/              — xUnit: парсер, cookie+307, генератор конфига, ACL канала, smoke-тесты с реальным sing-box
installer/          — Inno Setup
build/              — скрипты сборки дистрибутивов
```

### Как работает per-app

1. UI отдаёт службе список выбранных `.exe` и режим.
2. Служба генерирует `config.json`, где в `route.rules` есть правила:
   `{"process_name": [...], "action": "route", "outbound": "proxy"}`,
   `{"process_path": [...]}` и `{"process_path_regex": ["(?i)^C:\\...\\"]}` (дочерние процессы).
3. `route.final` = `direct` для режима «только выбранные», `proxy` — для «все, кроме выбранных».
4. `route.find_process: true` обязателен: sing-box определяет процесс-владелец соединения.
5. Свои процессы (`sing-box.exe`, `VpnUs*.exe`) и службы обновления Windows всегда идут напрямую.

**Ограничения per-app**: правила работают по процессу-владельцу соединения. Службы Windows,
антивирус, процессы других пользователей, UWP/Store-приложения и трафик ядра могут не попадать
под правила. Диагностика — Clash API: `http://127.0.0.1:9090/connections` показывает `chains` и `rule`
для каждого соединения.

## Данные и пути

| Что | Обычный режим | Портативный режим |
|---|---|---|
| Настройки/серверы/конфиг/ядро | `C:\ProgramData\VpnUs\` | `<папка>\data\` |
| Настройки UI, лог UI | `%AppData%\VpnUs\` | `<папка>\data\ui\` |
| Логи службы | `C:\ProgramData\VpnUs\logs\service.log` | `<папка>\data\logs\` |
| Лог установки службы | `C:\ProgramData\VpnUs\logs\install.log` | — |

Служба: `sc qc VpnUs` (`binPath`), управление — `VpnUs.Service.exe install|uninstall|console`.

## Обновления

- **Ядро** (sing-box + wintun): Настройки → «Обновить ядро» — тянет последний релиз `SagerNet/sing-box`
  под вашу архитектуру в `...\core\`.
- **Приложение**: Настройки → «Проверить обновления» (и авто-проверка при запуске).
  Установленная версия обновляется через `VpnUs-Setup-*.exe` (тихий режим, UAC),
  портативная — распаковкой ZIP после выхода приложения.
- Релизы создаются по тегу:

```powershell
git tag v1.0.1; git push origin v1.0.1     # .github/workflows/release.yml соберёт x64+arm64 и опубликует Release
```

Артефакты в релизе должны называться так (на это опирается автообновление):
`VpnUs-Setup-<ver>-x64.exe`, `VpnUs-Setup-<ver>-arm64.exe`,
`VpnUs-portable-<ver>-x64.zip`, `VpnUs-portable-<ver>-arm64.zip`.

## Диагностика

- Логи ядра и службы: Настройки → «Открыть папку данных» → `logs\service.log`, либо экран **«Логи»**.
- Интерфейс не видит службу: Настройки → «Установить / починить службу (UAC)» → «Проверить»
  (в строке «Запущена из» должен быть путь к `VpnUs.Service.exe`).
- Занят порт Clash API (9090) — смените порт в Настройках.
- Нет прав на TUN — sing-box в службе должен работать от `LocalSystem`.

## Проверка качества

`dotnet test` прогоняет 66 тестов, включая:
- парсинг всех схем и пропуск мусора/невалидных UUID;
- подписку с `307 + Set-Cookie` (поднимается локальный HTTP-сервер);
- генерацию конфига во всех режимах и `sing-box check` **реальным бинарником**, если он установлен;
- рантайм-тест: sing-box запускается с нашим конфигом (TUN подменён на mixed-in) и отвечает Clash API.
