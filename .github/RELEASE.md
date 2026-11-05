# Server release review and recovery

Pushing a `v*` tag creates a **draft** release. CI builds the executable once,
uploads it before the checksum (older clients select the first asset), and
compares GitHub's executable digest with the CI build's SHA-256. Review the
draft and pin Horizon to that digest before publishing that same draft. Do not
rebuild or replace its executable after pinning it.

Inspect a draft in PowerShell with:

```powershell
$tag = 'v2.4.0' # Replace with the actual tag.
$release = gh release view $tag --repo megabytesme/MineLights-Server --json isDraft,assets | ConvertFrom-Json
$release.isDraft
$release.assets | Select-Object name,digest,state
```

Before publishing, confirm the draft is still a draft, the first asset is
`MineLights.exe`, there is exactly one executable and one checksum asset, and
the executable's `sha256:` digest equals both the CI build log and the Horizon
pin. CI failure leaves the release as a draft; do not rerun the workflow for
the same tag, since the workflow intentionally refuses existing releases.

If upload of the checksum failed but the draft contains **only** the executable
as its first asset and its GitHub digest matches the SHA-256 printed by that
tag's successful build step, the missing sidecar can be recovered without
replacing the executable. With `$release` and `$tag` from above:

```powershell
if (-not $release.isDraft -or @($release.assets).Count -ne 1 -or $release.assets[0].name -ne 'MineLights.exe' -or $release.assets[0].digest -notmatch '^sha256:([0-9a-f]{64})$') { throw 'Draft is not eligible for checksum-only recovery.' }
$hash = $Matches[1]
$expectedCiHash = 'REPLACE_WITH_HASH_PRINTED_BY_THE_TAGGED_CI_RUN'
if ($hash -cne $expectedCiHash) { throw 'GitHub digest does not match the tagged CI run.' }
$checksumPath = Join-Path $env:TEMP 'MineLights.exe.sha256'
"$hash  MineLights.exe" | Set-Content -LiteralPath $checksumPath -Encoding ascii
gh release upload $tag $checksumPath --repo megabytesme/MineLights-Server
if ($LASTEXITCODE -ne 0) { throw 'Checksum upload failed; leave the release as a draft.' }
```

Inspect the draft again and perform all checks above. If its first asset is not
the executable, the executable is absent, the digest differs from the CI log,
or an existing asset would have to be replaced, abandon the draft and use a new
version and tag. Do not publish it or replace assets that may already be pinned.
