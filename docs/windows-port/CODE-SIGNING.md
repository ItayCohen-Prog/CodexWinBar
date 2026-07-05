# Code signing CodexWinBar

CodexWinBar currently ships **unsigned**, so on first run Windows SmartScreen shows an "Unknown
publisher" warning and users must click **More info → Run anyway**. Signing the installer removes that
wording and lets SmartScreen build reputation for your identity so the warning fades.

The build is already sign-ready: `build/pack-windows.ps1` accepts `-AzureTrustedSignFile`, `-SignParams`,
or `-SignTemplate` and passes them to `vpk`.

---

## Why it's not "just buy a .pfx"

Since June 2023, code-signing private keys must live on certified hardware (USB token / HSM) or in a
cloud signing service — no more downloadable `.pfx`. And **your location matters**: Microsoft's cheap
Azure Artifact Signing is **not available in Israel** (individuals: USA/Canada only; organizations:
USA/Canada/EU/UK). So for an Israeli developer the realistic options are:

| Option | Cost (approx) | Hardware? | CI-friendly | Notes |
|---|---|---|---|---|
| **Certum Open Source Code Signing** (SimplySign cloud) | **~$100/yr** | No (cloud) | Manual-ish | **Recommended for us** — purpose-built for OSS (CodexWinBar is MIT), cheapest, Microsoft-trusted. |
| **SSL.com OV + eSigner** (cloud HSM) | ~$65/yr cert + ~$20/mo eSigner | No (cloud) | **Yes** | Best if you want clean GitHub Actions signing. |
| Azure Artifact Signing | ~$10/mo | No (cloud) | Yes | ❌ Not available in Israel. |
| EV certificate (token) | ~$300–700/yr | Yes | Hard | Only one that clears SmartScreen **instantly**; hardware token. |

Note: OV/open-source certs build SmartScreen reputation gradually (over downloads); only **EV** is instant.
A CA/Browser Forum rule (from March 2026) caps public code-signing certs at 458 days, so expect ~yearly renewal.

---

## Recommended: Certum Open Source Code Signing (cloud)

Cheapest, fits our MIT license, no hardware, Microsoft-trusted. Available internationally (Certum is an
EU CA that issues worldwide, Israel included).

### One-time setup
1. Buy an **Open Source Code Signing** certificate on **SimplySign (cloud)** from
   [certum.store](https://certum.store/open-source-code-signing-on-simplysign.html) (or a reseller).
2. **Identity verification** — Certum needs proof of identity (a full/notarized copy of your ID),
   a utility bill, and **the URL of your active open-source project**
   (`https://github.com/ItayCohen-Prog/CodexWinBar`). Send to `ccp@certum.pl` per their instructions.
3. Once issued, **activate the certificate on SimplySign** and install **SimplySign Desktop** — it logs
   into the cloud and loads your certificate into the Windows certificate store.
4. Find the certificate **SHA-1 thumbprint**: `Get-ChildItem Cert:\CurrentUser\My | Format-List Subject, Thumbprint`

### Sign
With SimplySign Desktop connected, sign via signtool params:

```powershell
./build/pack-windows.ps1 -SignParams '/fd sha256 /tr http://time.certum.pl /td sha256 /sha1 <YOUR_THUMBPRINT>'
```

(For CI, Certum's cloud signing runs through their SimplySign tooling — doable but more manual than
SSL.com; many OSS projects just sign the release locally.)

---

## Alternative: SSL.com OV + eSigner (best for CI)

If you'd rather have hands-off GitHub Actions signing:

1. Buy an **OV Code Signing** certificate (~$64.50/yr) from [ssl.com](https://www.ssl.com/products/software-integrity/code-signing/ov/)
   and add the **eSigner** cloud subscription (~$20/mo). Complete their identity validation.
2. Two ways to sign, both wired into our script:
   - **eSigner CKA** (installs a Windows KSP so `signtool` sees the cloud cert):
     `./build/pack-windows.ps1 -SignParams '/fd sha256 /tr http://ts.ssl.com /td sha256 /sha1 <THUMBPRINT>'`
   - **CodeSignTool** (a CLI, ideal for CI) via the template mode:
     ```powershell
     ./build/pack-windows.ps1 -SignTemplate 'CodeSignTool.bat sign -input_file_path={{file}} -credential_id=$CRED -username=$USER -password=$PASS -totp_secret=$TOTP -override'
     ```
3. In CI, store the eSigner credentials as GitHub secrets and add a signing step before the pack step in
   `.github/workflows/release-windows.yml`, passing `-SignTemplate` (see SSL.com's GitHub Actions guide).

---

## After you're signed

- Re-cut the release (a signed `Setup.exe` has a new hash) and **update the winget manifest's
  `InstallerSha256`** — see `packaging/winget/README.md`.
- Update the README: drop the "Unknown publisher / Run anyway" note.
- SmartScreen reputation still accrues with downloads; if prompts persist, submit the signed file at
  <https://www.microsoft.com/wdsi/filesubmission>.
