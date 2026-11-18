# Minecraft API access for Eclipse

Earlier real-account diagnostics confirmed successful Microsoft OAuth, Xbox Live and XSTS responses. Minecraft Services then returned HTTP 403 with `Invalid app registration` for the configured application Client ID.

If this response still occurs, the application owner needs Minecraft Java Game Service API approval. Entra redirect settings and a player's game license do not replace application approval.

- Approval request: https://aka.ms/mce-reviewappid
- Service documentation: https://aka.ms/AppRegInfo

## Application details

- **Name:** Eclipse.
- **Application (client) ID:** `44451aab-9b60-4e9d-8d73-c08762cdb1be`.
- **Platform:** native Windows desktop launcher, .NET 10 / WinForms. The release launcher is x86 and starts a separate x64 Java process.
- **Client type:** public client; no embedded client secret.
- **Account type:** personal Microsoft accounts.
- **Redirect URI:** `http://localhost` under **Mobile and desktop applications**. The launcher selects an ephemeral port.
- **Authentication:** authorization code + PKCE S256 in the system browser, with a random state checked locally.
- **Scopes:** `XboxLive.signin offline_access`.
- **Microsoft mode:** verifies Minecraft Java ownership and the player profile before each launch; downloads official game files; displays and updates the player's own skins and owned capes.
- **Local mode:** a separate nickname-based profile, explicitly selected in Settings, with no Microsoft credentials or ownership claim.
- **Credential handling:** passwords are entered only on Microsoft's website. Refresh and Minecraft tokens are encrypted with Windows DPAPI for the current user. No token logging or server-side credential collection.
- **Observed error:** HTTP 403 / `Invalid app registration` at `https://api.minecraftservices.com/authentication/login_with_xbox`, after successful Xbox/XSTS authentication.
- **Source:** https://github.com/deluti/eclipse-launcher.
- **Contact name and email:** the application owner must provide their own contact information.

This file does not submit an approval request. The owner should verify the details before submitting. Once the same Client ID is approved, the launcher can retry using the encrypted saved sign-in; rebuilding is unnecessary.

For `invalid_request / redirect_uri is not valid`, open **Microsoft Entra → App registrations → the application → Authentication**, add **Mobile and desktop applications** with `http://localhost`, enable **Allow public client flows**, and save.
