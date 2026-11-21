# Charli Vision Windows Authenticode signing (optional, fail-closed).
# Requires an authorized certificate with Code Signing EKU and private key;
# never searches for or exports private key material into the repository.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [string]$TimestampServer='http://timestamp.digicert.com'
)
$ErrorActionPreference='Stop'
$resolved=(Resolve-Path -LiteralPath $InstallerPath).Path
if(-not $resolved.EndsWith('.exe',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Nur ein Windows .exe-Installer darf signiert werden.'
}
$thumb=$CertificateThumbprint.Replace(' ','').ToUpperInvariant()
$cert=$null;$machine=$false
foreach($item in @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue)){
    if($item.Thumbprint -eq $thumb){$cert=$item;break}
}
if(-not $cert){
    foreach($item in @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue)){
        if($item.Thumbprint -eq $thumb){$cert=$item;$machine=$true;break}
    }
}
if(-not $cert){throw 'Kein passendes Code-Signing-Zertifikat gefunden.'}
if(-not $cert.HasPrivateKey -or $cert.NotBefore -gt (Get-Date) -or
   $cert.NotAfter -le (Get-Date) -or
   -not @($cert.EnhancedKeyUsageList | Where-Object {
        $_.FriendlyName -eq 'Code Signing' -or
        $_.ObjectId -eq '1.3.6.1.5.5.7.3.3'
   }).Count){
    throw 'Code-Signing-Zertifikat ist nicht gueltig oder nicht freigegeben.'
}
$signTool=(Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if(-not $signTool){throw 'Windows SDK signtool.exe fehlt.'}
$args=@('sign','/fd','SHA256','/td','SHA256','/tr',
    $TimestampServer,'/sha1',$thumb,'/s','My')
if($machine){$args+='/sm'}
$args+=$resolved
& $signTool @args
if($LASTEXITCODE -ne 0){throw "Authenticode signing failed: $LASTEXITCODE"}
$sig=Get-AuthenticodeSignature -LiteralPath $resolved
if($sig.Status -ne 'Valid'){
    throw "Code-Signing-Nachpruefung fehlgeschlagen: $($sig.Status)"
}
Write-Output 'AUTHENTICODE=VALID'
Write-Output ('SHA256='+((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash))
