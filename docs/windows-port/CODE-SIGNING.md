# Code signing CodexWinBar

Right now CodexWinBar ships **unsigned**, so on first run Windows SmartScreen shows an "Unknown
publisher" warning and users must click **More info → Run anyway**. Signing the installer removes the
"unknown publisher" wording and lets SmartScreen build reputation for your identity so the warning fades.

This guide covers how to get a certificate and wire it into our build. The build is already sign-ready:
`build/pack-windows.ps1` takes `-AzureTrustedSignFile` or `-SignParams` and passes them to `vpk`.

---

## Why it's not as simple as "buy a .pfx"

Since June 2023, Microsoft requires code-signing private keys to live on certified hardware (an HSM or
USB token) or in a cloud signing service. You can no longer just download a `.pfx` file and sign with it.
That leaves three realistic paths:

| Option | Cost | Hardware? | SmartScreen | Notes |
|---|---|---|---|---|
| **Azure Artifact Signing** (formerly Trusted Signing) | **~$10/mo** | No (cloud) | Builds reputation over time | **Recommended.** Cloud-based, CI-friendly, cheapest. |
| Traditional OV certificate (Sectigo/DigiCert/SSL.com) | ~$200–500/yr | Yes (token or cloud HSM) | Builds reputation over time | More setup; the token complicates CI. |
| EV certificate | ~$300–700/yr | Yes (hardware token) | **Instant** reputation | Priciest; hardware token can't easily run in CI. |

**⚠️ Eligibility:** Azure Artifact Signing "Public Trust" certificates are available to **individual
developers only in the USA and Canada** (organizations also in the EU/UK). If you're elsewhere as an
individual, you'd need a registered business or a traditional cert. Check the current list before you
start: <https://learn.microsoft.com/azure/artifact-signing/>

---

## Recommended: Azure Artifact Signing

### One-time setup
1. Create a free **Azure account** (<https://azure.microsoft.com>) and a subscription.
2. In the Azure Portal, create an **Artifact Signing** (Trusted Signing) account.
3. Create an **Identity Validation** request and complete it — for an individual this verifies your
   legal name/identity (can take a few days). This name becomes your publisher name in the signature.
4. Create a **Certificate Profile** (type: *Public Trust*) once identity is validated.
5. Note three values from the portal: the **account endpoint** (e.g. `https://eus.codesigning.azure.net/`),
   the **account name**, and the **certificate profile name**.

Follow Microsoft's current step-by-step (the portal UI shifts):
<https://learn.microsoft.com/azure/artifact-signing/quickstart>

### Create the metadata file
Save this as e.g. `build/azure-signing.json` (do **not** commit it — add to `.gitignore`):

```json
{
  "Endpoint": "https://eus.codesigning.azure.net/",
  "CodeSigningAccountName": "your-account-name",
  "CertificateProfileName": "your-profile-name"
}
```

### Sign locally
Authenticate once with the Azure CLI (`az login`), then:

```powershell
./build/pack-windows.ps1 -AzureTrustedSignFile .\build\azure-signing.json
```

Velopack signs every executable and the `Setup.exe` with your cloud certificate.

### Sign in CI (GitHub Actions)
1. Create an Azure **service principal** with the *Trusted Signing Certificate Profile Signer* role on
   the signing account, and add these as repository **secrets**:
   `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.
2. In `.github/workflows/release-windows.yml`, before the pack step, write the metadata file and export
   the credentials, then pass the file to the pack script:

   ```yaml
   - name: Write signing metadata
     shell: pwsh
     run: |
       @'
       { "Endpoint": "https://eus.codesigning.azure.net/",
         "CodeSigningAccountName": "your-account",
         "CertificateProfileName": "your-profile" }
       '@ | Set-Content build/azure-signing.json
   - name: Build & pack installer
     shell: pwsh
     env:
       AZURE_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
       AZURE_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
       AZURE_CLIENT_SECRET: ${{ secrets.AZURE_CLIENT_SECRET }}
     run: ./build/pack-windows.ps1 -Version ${{ steps.ver.outputs.version }} -AzureTrustedSignFile build/azure-signing.json
   ```

   (Velopack uses `DefaultAzureCredential`, which picks up those env vars automatically.)

---

## Alternative: a traditional certificate (signtool)

If you buy an OV/EV cert on a token or cloud HSM, pass signtool parameters instead:

```powershell
./build/pack-windows.ps1 -SignParams '/fd sha256 /td sha256 /tr http://timestamp.digicert.com /sha1 <THUMBPRINT>'
```

`vpk` runs `signtool.exe sign <params> <file>` on each file. The exact params depend on your CA/token
(cloud HSMs like SSL.com eSigner ship their own signing tool or KSP; follow their docs).

---

## After you're signed

- Re-cut the release (the signed `Setup.exe` has a new hash) and **update the winget manifest's
  `InstallerSha256`** (see `packaging/winget/README.md`).
- Update the README: drop the "Unknown publisher / Run anyway" note.
- SmartScreen reputation still accrues with downloads even when signed; if prompts persist, submit the
  signed file at <https://www.microsoft.com/wdsi/filesubmission>.
