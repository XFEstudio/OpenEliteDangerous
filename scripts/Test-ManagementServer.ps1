param(
    [Parameter(Mandatory=$true)][string]$DataRoot,
    [string]$Address='http://127.0.0.1:48081/api/',
    [string]$Account
)
$ErrorActionPreference='Stop'
$taskAccess=Get-Content -LiteralPath (Join-Path $DataRoot 'admin-access.json') -Raw | ConvertFrom-Json
$taskResults=[Collections.Generic.List[object]]::new()
function Call-TaskApi([string]$Operation,$Arguments=@{},[string]$Token='', [int]$Expected=200) {
    $taskHeaders=@{}
    if($Token){$taskHeaders.Authorization='Bearer '+$Token}
    $taskResponse=Invoke-WebRequest -Uri ($Address+'control') -Method Post -ContentType 'application/json' -Headers $taskHeaders -Body (@{operation=$Operation;arguments=$Arguments} | ConvertTo-Json -Depth 80 -Compress) -SkipHttpErrorCheck
    if([int]$taskResponse.StatusCode -ne $Expected){throw ($Operation+' expected '+$Expected+', received '+$taskResponse.StatusCode+': '+$taskResponse.Content)}
    $taskResults.Add(@{operation=$Operation;expected=$Expected;passed=$true})
    ConvertFrom-Json -InputObject $taskResponse.Content -AsHashtable
}
function Check-Task([bool]$Condition,[string]$Name) {
    if(-not $Condition){throw ('Check failed: '+$Name)}
    $taskResults.Add(@{check=$Name;passed=$true})
}
try {
    $taskHealth=Invoke-RestMethod -Uri ($Address+'health')
    Check-Task ($taskHealth.framework -eq 'XFEExtension.NetCore.ServerInteractive') 'Real ServerInteractive HTTP listener'
    $null=Call-TaskApi 'profile' @{} '' 401
    $null=Call-TaskApi 'auth/local-owner' @{} 'invalid-local-key' 401
    $taskOwner=Call-TaskApi 'auth/local-owner' @{} $taskAccess.key
    $taskToken=$taskOwner.token
    $taskAccounts=Call-TaskApi 'accounts' @{} $taskToken
    if(-not $Account){$Account=@($taskAccounts | Where-Object {$_.email -ne 'local-owner@xfe.local'})[0].id}
    $taskInventory=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    $taskNative=Call-TaskApi 'native/export' @{accountId=$Account} $taskToken
    $taskCredits=[long]$taskInventory.credits+12345
    $null=Call-TaskApi 'inventory/edit' @{accountId=$Account;edit='credits';values=@{delta=12345};version=$taskInventory.version} $taskToken
    $null=Call-TaskApi 'inventory/edit' @{accountId=$Account;edit='credits';values=@{delta=1};version=$taskInventory.version} $taskToken 409
    $taskInventory=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    Check-Task ($taskInventory.credits -eq $taskCredits) 'Credit edit persisted without stale overwrite'
    $null=Call-TaskApi 'inventory/edit' @{accountId=$Account;edit='cargo';values=@{itemId='platinum';category='cargo';quantity=3};version=$taskInventory.version} $taskToken
    $taskInventory=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    $null=Call-TaskApi 'materials/prepare' @{accountId=$Account;version=$taskInventory.version} $taskToken
    $taskInventory=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    $taskCatalog=Call-TaskApi 'catalog' @{accountId=$Account;search=''} $taskToken
    $taskMaterial=@($taskCatalog | Where-Object {$_.category -in @('Raw','Encoded','Manufactured')})[0]
    Check-Task ($null -ne $taskMaterial) 'Native material catalogue is available'
    $null=Call-TaskApi 'inventory/edit' @{accountId=$Account;edit='materials';values=@{itemId=$taskMaterial.id;category=$taskMaterial.category;quantity=5};version=$taskInventory.version} $taskToken
    $taskInventory=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    $null=Call-TaskApi 'inventory/edit' @{accountId=$Account;edit='materials';values=@{itemId=$taskMaterial.id;category=$taskMaterial.category;quantity=1001};version=$taskInventory.version} $taskToken 400
    $taskAfter=Call-TaskApi 'commander' @{accountId=$Account} $taskToken
    Check-Task ($taskAfter.version -eq $taskInventory.version) 'Rejected material edit is atomic'
    $taskFleet=Call-TaskApi 'fleet' @{accountId=$Account} $taskToken
    $taskShip=@($taskFleet.ships | Where-Object {$_.active})[0]
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='clone';templateShipId=$taskShip.id;name='HTTP Test Ship';ident='HTTP-01';version=$taskAfter.version} $taskToken
    $taskFleet=Call-TaskApi 'fleet' @{accountId=$Account} $taskToken
    $taskClone=@($taskFleet.ships | Where-Object {$_.label.ident -eq 'HTTP-01'})[-1]
    Check-Task ($taskClone.id -ne $taskShip.id -and -not $taskClone.active) 'New private vessel has independent identity'
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='select';shipId=$taskClone.id;version=$taskFleet.commander.version} $taskToken
    $taskFleet=Call-TaskApi 'fleet' @{accountId=$Account} $taskToken
    Check-Task (@($taskFleet.ships | Where-Object {$_.active})[0].id -eq $taskClone.id) 'Active vessel selection persisted'
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='remove';shipId=$taskClone.id;version=$taskFleet.commander.version} $taskToken 400
    $taskWeapon=@($taskClone.slots | Where-Object {$_.name -match 'Hardpoint' -and $null -ne $_.module})[0]
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='store';shipId=$taskClone.id;slot=$taskWeapon.name;version=$taskFleet.commander.version} $taskToken
    $taskFleet=Call-TaskApi 'fleet' @{accountId=$Account} $taskToken
    $taskStored=@($taskFleet.stored)[-1]
    Check-Task ($taskStored.module.name -eq $taskWeapon.module.name) 'Weapon moved into persistent module warehouse'
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='retrieve';shipId=$taskClone.id;slot=$taskWeapon.name;storageId=$taskStored.slotId;version=$taskFleet.commander.version} $taskToken
    $taskFleet=Call-TaskApi 'fleet' @{accountId=$Account} $taskToken
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='module';shipId=$taskClone.id;slot='PowerPlant';module=$taskWeapon.module.name;version=$taskFleet.commander.version} $taskToken 400
    $taskExport=Call-TaskApi 'native/export' @{accountId=$Account} $taskToken
    Check-Task ($taskExport.snapshot.credits -eq $taskCredits) 'Native snapshot contains edited credits'
    Check-Task (@($taskExport.materials | Where-Object {$_.id.ToString() -eq $taskMaterial.id})[0].quantity -eq 5) 'Native material quantity survives fleet edits'
    $taskOriginal=@($taskExport.snapshot.vessels | Where-Object {$_.id -eq $taskShip.id})[0]
    Check-Task (@($taskOriginal.cargo | Where-Object {$_.commodity -eq 'platinum' -and $_.quantity -eq 3}).Count -gt 0) 'Native cargo contains authored warehouse item'
    $taskEmail='integration-'+[Guid]::NewGuid().ToString('N')+'@example.invalid'
    $taskPassword=[Guid]::NewGuid().ToString('N')
    $null=Call-TaskApi 'auth/register' @{email=$taskEmail;name='HTTP Player';password=$taskPassword}
    $taskPlayer=Call-TaskApi 'auth/login' @{email=$taskEmail;password=$taskPassword}
    $null=Call-TaskApi 'accounts' @{} $taskPlayer.token 403
    $null=Call-TaskApi 'commander' @{accountId=$Account} $taskPlayer.token 403
    $null=Call-TaskApi 'fleet/edit' @{accountId=$Account;action='clone';templateShipId=$taskShip.id;name='Forbidden';ident='NO';version=$taskFleet.commander.version} $taskPlayer.token 403
    $null=Call-TaskApi 'auth/logout' @{} $taskPlayer.token
    $null=Call-TaskApi 'profile' @{} $taskPlayer.token 401
    $null=Call-TaskApi 'auth/logout' @{} $taskToken
    [IO.File]::WriteAllText((Join-Path $DataRoot 'integration-report.json'),(@{passed=$true;checks=$taskResults;account=$Account;expectedCredits=$taskCredits;materialId=$taskMaterial.id;selectedShipId=$taskClone.id} | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output ('PASS '+$taskResults.Count+' real HTTP checks: authentication, roles, credits, cargo, materials, fleet, module storage, conflicts and atomic rejection.')
} catch {
    [IO.File]::WriteAllText((Join-Path $DataRoot 'integration-report.json'),(@{passed=$false;checks=$taskResults;error=$_.Exception.Message} | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    throw
}
