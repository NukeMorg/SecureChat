# SecureChat Enterprise

Корпоративный мессенджер со сквозным шифрованием (E2E): личные чаты (Direct) и групповые каналы с распределением группового ключа через 1-1 E2E и Sender Keys ratchet. WPF-клиент (Windows) + сервер на ASP.NET Core (SignalR) + SQLite.

- **Транспорт:** SignalR (`/hub`), авто-реконнект 1с→30с, статусы `Sent → Delivered → Read`
- **Крипто-стек (единый, без внешних крипто-библиотек):** RSA-2048 + AES-256-GCM + HMAC-SHA256
- **Роли:** `Admin` / `Manager` (внутренний код роли) / `Employee`
- **Логи:** всегда English, `InvariantCulture`, ISO 8601 UTC; аудит — файл + таблица `AuditLog` (append-only)
- **Документация:** только этот `README.md`

---

## 1. Архитектура

```
SecureChat.slnx
├── Core/SecureChat.Core            # DTO протокола, валидаторы, ChatAggregator,
│                                   #   ChannelCrypto (ratchet), CryptoService (RSA/AES-GCM),
│                                   #   Excel/Word рендеры, санитайзеры
├── Security/SecureChat.Security    # PasswordHasher (BCrypt 12 + легаси PBKDF2), PiiMasker
├── Server/                         # ASP.NET Core: ChatHub, Database (SQLite), EmailService
├── Client/                         # WPF: MainWindow, LoginWindow, сервисы (Network, DPAPI, аватары)
└── Tests/SecureChat.Tests          # xUnit: 66 тестов (валидаторы, крипто, security, round-trip)
```

- **Клиент** — code-behind WPF, тёмная/светлая темы (`Themes/*.xaml`, `DynamicResource`).
- **Сервер** — `WebApplication` + `MapHub<ChatHub>("/hub")`; SQLite через `Microsoft.Data.Sqlite` (параметризованные запросы); SMTP-коды подтверждения (dev-режим — код в лог).
- **Единый список чатов**: контакты и каналы в одном списке (как в Telegram), шаблоны через `ChatItemTemplateSelector`.

### Модель E2E

**Direct:** сессионный AES-ключ диалога шифруется RSA-2048 получателю (`EncryptedKey`) и себе (`EncryptedKeySelf` — расшифровка своей истории). Сообщение — AES-256-GCM.

**Channel:** `GroupKey` (32 байта CSPRNG) генерируется Owner локально и передаётся каждому участнику через 1-1 E2E (RSA + AES-GCM) — `GroupKeyDelivery`. Сервер хранит только RSA-блоки (`ChannelKeys`) для восстановления после перезапуска клиента, **открытый ключ серверу недоступен**.

**Sender Keys ratchet (HMAC-SHA256):**
```
ChainKey₀(sender) = HMAC-SHA256(GroupKey, "chain" || senderId)
MessageKey(i)     = HMAC-SHA256(ChainKey(i), 0x02)
ChainKey(i+1)     = HMAC-SHA256(ChainKey(i), 0x01)
```
Шифрование сообщения — AES-256-GCM на `MessageKey(i)`, в сообщении — `ChainIteration`. Приём: «догон» цепочки до итерации, replay (итерация позади курсора) — reject. Ротация GroupKey — на add/remove/ban участника (backward/forward secrecy), все цепочки сбрасываются.

Сервер видит только: `ChannelId`, `SenderId`, `ChainIteration`, шифртекст, метаданные.

## 2. Схема БД (SQLite, миграции через `PRAGMA user_version`)

| Таблица | Назначение |
|---|---|
| `Clients` | Id, Email (uniq), PasswordHash (BCrypt), Salt (легаси), PublicKey, UserName, **Role** (`Admin`/`Manager`/`Employee`), **IsActive**, **Status**, **AvatarPath** |
| `Sessions` | Token (GUID), UserId, CreatedAt, **ExpiresAt** (7 дней), **IsRevoked** |
| `EmailVerifications` | Код подтверждения (TTL 10 мин), single-use |
| `EmailRateLimits` | 3 письма / 15 мин на email |
| `LoginAttempts` | Неудачные входы: 5 / 15 мин на email ИЛИ IP |
| `Messages` | MessageId (uniq), SenderId, ReceiverId (Direct) / **ChannelId** (канал), EncryptedKey(+Self), Nonce/Ciphertext/Tag, ContentType, FileName/Size, **ChainIteration**, **ReplyToMessageId**, **IsEdited/EditedAt**, **DeletedAt**, Timestamp (ISO 8601 UTC), Status |
| `Contacts` | Симметричная пара (UserId, ContactId) |
| `Channels` / `ChannelMembers` | Id (GUID), Name (uniq), OwnerId; участники (Role: Owner/Member) |
| `ChannelKeys` | Зашифрованные RSA-копии GroupKey per member (Version) |
| `AuditLog` | **Append-only** (триггеры запрещают UPDATE/DELETE): Timestamp UTC, ActorId, Action, TargetId, Result, IpAddress |

Миграции идемпотентны: v1 (роли/аудит), v2 (каналы), v3 (HR→Manager).

## 3. Протокол обмена (SignalR, `/hub`)

**Client → Server (хаб-методы):** `SendEmailCode`, `Login`, `RegisterAccount`, `Hello` (только по токену), `Logout`, `SendMessage`, `Ack(Delivered/Read)`, `GetHistory` (курсор 50), `GetContacts`, `GetPublicKey`, `GetUserInfo`, `AddContact`, `UpdateProfile`, `UpdateStatus`, `UploadAvatar`, `GetUserProfile`, `EditMessage` (Direct, окно 15 мин), `DeleteMessage` (soft), `BanUser`/`UnbanUser`/`ChangeRole`/`GetAuditLog` (Admin), `CreateChannel`/`GetChannels`/`GetChannelMembers`/`JoinChannel`/`LeaveChannel`/`AddChannelMember`/`RemoveChannelMember` (Manager/Admin), `SendGroupKeyDelivery`, `SendChannelKeyRotated`, `SendChannelMessage`, `GetChannelHistory`.

**Server → Client (события):** `IncomingMessage`, `IncomingChannelMessage`, `DeliveryStatus`, `UserOnline/Offline`, `ContactList`, `HistoryResponse`, `PublicKeyResponse`, `UserInfoResponse`, `UserProfileUpdated`, `AddContactResponse`, `SendEmailCodeResponse`, `LoginResponse`, `RegisterAccountResponse`, `HelloResponse`, `LogoutResponse`, `Error`, `Banned`, `GroupKeyDelivery`, `ChannelKeyRotated`, `ChannelMemberAdded/Removed`, `MessageEdited`, `MessageDeleted`.

Валидация — на сервере (`Core/Validators`): email без ограничений домена (`RegexOptions.NonBacktracking`, ReDoS-safe), пароль ≥8 (буква+цифра), UserName 1–100, текст 1–4000, лимиты шифртекста (текст 16 500 / файл 14 000 000 base64-символов ≈ 10 МБ), запрет управляющих символов кроме `\n`/`\t`. NFC-нормализация — на клиенте до шифрования.

## 4. Запуск

**Dev:**
```
dotnet run --project Server        # http://*:9000, /health → Healthy
dotnet run --project Client        # WPF-клиент
dotnet test Tests/SecureChat.Tests.csproj
```
Конфигурация `appsettings.json` (Client и Server) хранится в репозитории и не содержит секретов. SMTP-пароль — только через secrets:
```
cd Server && dotnet user-secrets set "Smtp:SenderPassword" "..."
```
Без реального SMTP (`UseRealSmtp: false`) код подтверждения печатается в лог сервера (dev-режим).

## 5. Сборка инсталлятора (Inno Setup)

```
dotnet publish Client -c Release -r win-x64 --self-contained
```
Скрипт Inno Setup: установка в Program Files, ярлыки, деинсталлятор. Code signing — самоподписанный сертификат для демо (`signtool sign /f cert.pfx`).

## 6. Локализация и интернационализация (i18n/L10n)

Проект разделён два процесса в соответствии с индустриальной методологией:

**Интернационализация (i18n, инженерная база — сделана разработчиком):**
- все строки UI вынесены из кода в ресурсы: `Client/Resources/Strings.resx` (нейтральные, RU) + `Strings.en.resx` (культура `en`);
- доступ из XAML — `{Binding [Ключ], Source={x:Static local:LocalizationSource.Instance}}`, из кода — `LocalizationSource.Get("Ключ")`; смена языка на лету — `LocalizationSource.Refresh()` (INPC по `Item[]`);
- Unicode/UTF-8 повсеместно; NFC-нормализация текста до шифрования;
- форматы абстрагированы от кода: даты/числа — через `CultureInfo`/`DateTimeFormatInfo`/`NumberFormatInfo` текущей культуры потока; WPF-привязки культуро-зависимы (`FrameworkElement.Language` метаданные);
- единицы измерения — из ресурсов (`UnitBytes/UnitKilobytes/UnitMegabytes`), число — `FileSizeFormatter` (покрыт тестами на `ru-RU`/`en-US`);
- гибкий UI: авто-размеры, `TextWrapping`, `TextTrimming` — рассчитаны на разную длину перевода.

**Локализация (L10n, наполнение):**
- переводы живут только в ресурсных файлах — добавление языка = копия `Strings.resx` с суффиксом культуры (`Strings.de.resx`) + перевод значений, код не меняется;
- локаль хранится в полном формате ISO 639 + ISO 3166: **`ru-RU`, `en-US`** (`config.json → Language`), выбор — Вид → Настройки, применяется на лету (`CurrentCulture` + `CurrentUICulture` потока);
- конвенция семантических ключей: `Menu*` — корневые меню, `Mi*` — пункты меню, `Select*`/`Message*`/`No*` — тексты сообщений и состояний, `Unit*` — единицы измерения;
- логи и аудит **не локализуются** (всегда English, InvariantCulture, ISO 8601 UTC);
- форматирование данных (даты `g`, числа `N0`, размеры файлов) — только на отображении; на проводе и в БД — ISO 8601 UTC / InvariantCulture.

## 7. Политики

- **Пароли:** BCrypt, work factor 12, уникальная соль. Легаси-PBKDF2 (10 000, SHA-256) проверяется и прозрачно обновляется до BCrypt при первом успешном входе. Смена пароля отзывает все сессии.
- **Сессии:** GUID-токен, TTL 7 дней (`ExpiresAt`), `Logout`/бан — отзыв. Автологин — `Hello` строго по токену.
- **Rate limiting:** логин 5/15 мин (email OR IP), письма 3/15 мин, размер сообщения — лимит шифртекста.
- **DPAPI:** токен и приватный ключ в `%APPDATA%\SecureChat\config.json` шифруются `ProtectedData` (CurrentUser, префикс `dpapi:`).
- **Логи:** Serilog; файл JSON (10 МБ × 14, `logs/server-.log`), аудит отдельным файлом + `AuditLog`; всё English/UTC; PII маскируется (`PiiMasker`).
- **Экспорт/печать (только клиент):** Excel (ClosedXML, formula-injection префикс `'`, даты/числа `CurrentCulture`, ≤50 000 строк), Word (OpenXML, XXE: `DtdProcessing.Prohibit`+`XmlResolver=null`), печать (FlowDocument, номера страниц, водяной знак «Конфиденциально», опция «без вложений», XPS как PDF-альтернатива). Агрегаты — один `ChatAggregator`.

## 8. Модель угроз и остаточные риски

**Обеспечивается:** E2E Direct и Channel (сервер не видит plaintext и ключи); распределение/ротация GroupKey только через 1-1; replay-защита ratchet; брутфорс-защита; append-only аудит; секреты вне репо; DPAPI для локальных секретов; серверная валидация.

**Остаточные риски (осознанные ограничения):**
- Нет post-compromise security для каналов (не полный Double Ratchet); компрометация GroupKey раскрывает сообщения текущей версии ключа.
- Ротация GroupKey — только на add/remove/ban, не по расписанию; если Owner офлайн в момент бана — ротация при его возвращении.
- Транспорт `ws://` в локальной сети (в проде — TLS-терминация на reverse-proxy, `wss://`); HSTS — на прокси.
- RSA-паддинг PKCS#1 v1.5 (не OAEP) — сохранено для совместимости со старой историей.
- Редактирование сообщений — только Direct (ratchet каналов не позволяет перешифровать старую итерацию); файлы в каналах — только живые (история — по имени).
- Нет антивирус-скана вложений; серверная модерация отсутствует (E2E).
- Один инстанс сервера, без горизонтального масштабирования; SQLite (в проде — PostgreSQL).
- Экспорт/печать — только клиент; серверный экспорт — метаданные (Admin).
- Автологин хранит зашифрованный DPAPI-токен; компрометация учётной записи Windows раскрывает его.
