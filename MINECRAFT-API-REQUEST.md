# Доступ Eclipse к Minecraft Java Game Service API

На реальном аккаунте подтверждено: Microsoft OAuth — HTTP 200, Xbox Live — HTTP 200, XSTS — HTTP 200. Minecraft Services возвращает HTTP 403 с `Invalid app registration` для Client ID приложения.

Исправление регистра `Properties` / `RpsTicket` устраняет прежний HTTP 400 Xbox. Разрешение Minecraft API выдаётся отдельно на стороне Microsoft/Mojang, оно не добавляется локальным изменением JSON или настройкой redirect URI.

Страница запроса: https://aka.ms/mce-reviewappid

Справка, указанная самим Minecraft Services: https://aka.ms/AppRegInfo

## Данные для запроса

- Application name: **Eclipse**.
- Application (client) ID: **44451aab-9b60-4e9d-8d73-c08762cdb1be**.
- Platform: **Windows x64, native desktop application (.NET 10 / WinForms)**.
- Client type: **Public client, no embedded client secret**.
- Account types: **Personal Microsoft accounts**.
- OAuth redirect URI: **http://localhost**; ephemeral port selected at runtime.
- Authentication: **Authorization Code + PKCE S256**, system browser, random state checked locally.
- Scopes: **XboxLive.signin offline_access**.
- Purpose: **Licensed Minecraft Java Edition launcher. Authenticate the player's own Microsoft account, verify ownership and profile, download official game files, and launch Java. Display and update the player's own skin and owned cape through the authenticated profile API.**
- Credential handling: **Passwords are entered only on Microsoft's website. Refresh and Minecraft tokens are encrypted with Windows DPAPI for the current Windows user. No token logging or server-side credential collection.**
- Licensing: **Microsoft-account mode requires an online Minecraft ownership and profile check for each launch. A separate local nickname mode exists and does not use Microsoft credentials or claim to verify ownership.**
- API error: **HTTP 403 / Invalid app registration** at `https://api.minecraftservices.com/authentication/login_with_xbox` after successful Xbox/XSTS authentication.
- Contact name / email: **укажите свои данные владельца регистрации**.
- Project website / source link: **https://github.com/deluti/eclipse-launcher**.

Заявка не отправлена: контактные данные и сведения владельца должны быть проверены владельцем приложения. После разрешения этого же Client ID пересборка не нужна; повторите запуск, используя сохранённый вход.
