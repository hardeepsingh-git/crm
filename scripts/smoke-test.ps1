<#
.SYNOPSIS
  End-to-end smoke test against a running CRM API.
.DESCRIPTION
  Signs in as the seeded demo users, reads every module, then runs a full create/update/convert/close/delete
  workflow using records tagged "[E2E]" and removes them again (unless -Keep is given).
  The demo password is read from the API's user-secrets; if absent you are prompted for it.
.EXAMPLE
  ./scripts/smoke-test.ps1
  ./scripts/smoke-test.ps1 -BaseUrl http://localhost:4200 -Keep
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5290',
    [string]$ManagerEmail = 'manager@crm.local',
    [string]$RepEmail = 'rep@crm.local',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$script:passed = 0
$script:failed = 0

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        Write-Host "  PASS  $Name" -ForegroundColor Green -NoNewline
        if ($detail) { Write-Host "  ($detail)" -ForegroundColor DarkGray } else { Write-Host '' }
        $script:passed++
    } catch {
        Write-Host "  FAIL  $Name :: $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
}

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = $script:token) {
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; ContentType = 'application/json' }
    if ($Token) { $params.Headers = @{ Authorization = "Bearer $Token" } }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 5) }
    Invoke-RestMethod @params
}

function StatusOf([scriptblock]$Call) {
    try { & $Call | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
}

function Get-DemoPassword {
    $csproj = Join-Path $PSScriptRoot '..\backend\src\Crm.Api\Crm.Api.csproj'
    $id = ([xml](Get-Content $csproj)).Project.PropertyGroup.UserSecretsId | Where-Object { $_ } | Select-Object -First 1
    $path = Join-Path $env:APPDATA "Microsoft\UserSecrets\$id\secrets.json"
    if (Test-Path $path) {
        $script:secrets = Get-Content $path -Raw | ConvertFrom-Json
        $value = $script:secrets.'Seed:DemoUserPassword'
        if ($value) { return $value }
    }
    $secure = Read-Host -AsSecureString 'Demo user password'
    [Net.NetworkCredential]::new('', $secure).Password
}

$password = Get-DemoPassword
$tag = "[E2E $(Get-Date -Format 'HHmmss')]"
$today = Get-Date

Write-Host "`nCRM smoke test against $BaseUrl" -ForegroundColor Cyan

Write-Host "`n1. Health and authentication" -ForegroundColor Cyan
Check 'API liveness' { Assert ((Invoke-WebRequest "$BaseUrl/health/live" -UseBasicParsing).StatusCode -eq 200) 'not live' }
Check 'Database readiness' { Assert ((Invoke-WebRequest "$BaseUrl/health/ready" -UseBasicParsing).StatusCode -eq 200) 'DB not ready' }
Check 'Anonymous access is rejected' { $s = StatusOf { Api GET '/api/accounts' -Token $null }; Assert ($s -eq 401) "got $s"; '401' }
Check 'Wrong password is rejected' { $s = StatusOf { Api POST '/api/auth/login' @{ email = $RepEmail; password = 'Wrong#Pass123' } -Token $null }; Assert ($s -eq 401) "got $s"; '401' }

$login = $null
Check "Manager sign-in ($ManagerEmail)" {
    $script:login = Api POST '/api/auth/login' @{ email = $ManagerEmail; password = $password } -Token $null
    $script:token = $script:login.accessToken
    Assert ($script:login.user.roles -contains 'Manager') 'missing Manager role'
    $script:login.user.fullName
}
if (-not $script:token) { Write-Host "`nCannot continue without a session. Is demo data seeded (Seed:DemoData=true)?" -ForegroundColor Red; exit 1 }
$me = $script:login.user

Check 'Current user (/me)' { $m = Api GET '/api/auth/me'; Assert ($m.email -eq $ManagerEmail) 'wrong user'; $m.roles -join ',' }
Check 'Refresh token rotation' {
    $r = Api POST '/api/auth/refresh' @{ refreshToken = $script:login.refreshToken } -Token $null
    Assert ($r.refreshToken -ne $script:login.refreshToken) 'token not rotated'
    $s = StatusOf { Api POST '/api/auth/refresh' @{ refreshToken = $script:login.refreshToken } -Token $null }
    Assert ($s -eq 401) "old token reuse returned $s"
    $script:token = (Api POST '/api/auth/login' @{ email = $ManagerEmail; password = $password } -Token $null).accessToken
    'old token rejected after rotation'
}

Write-Host "`n2. Read every module (seeded data)" -ForegroundColor Cyan
Check 'Dashboard' {
    $d = Api GET '/api/dashboard'
    Assert ($d.accountCount -gt 0) 'no accounts - demo data missing?'
    "accounts=$($d.accountCount) contacts=$($d.contactCount) openLeads=$($d.openLeadCount) openDeals=$($d.openOpportunityCount) pipeline=$([math]::Round($d.openPipelineAmount)) winRate=$($d.winRate)%"
}
foreach ($module in 'accounts', 'contacts', 'leads', 'opportunities', 'activities') {
    Check "List $module" { $p = Api GET "/api/$module`?pageSize=5"; Assert ($p.totalCount -gt 0) 'empty'; "$($p.totalCount) records" }
}
Check 'Search accounts' { $p = Api GET '/api/accounts?search=acme'; Assert ($p.totalCount -ge 1) 'Acme not found'; $p.items[0].name }
Check 'Sort opportunities by amount desc' {
    $p = Api GET '/api/opportunities?sortBy=amount&sortDir=desc&pageSize=3'
    Assert ($p.items[0].amount -ge $p.items[-1].amount) 'not sorted'; "top=$($p.items[0].amount)"
}
Check 'Pipeline board' { $b = Api GET '/api/opportunities/pipeline'; Assert ($b.Count -gt 0) 'empty board'; "$($b.Count) cards" }
Check 'Overdue activities filter' { $p = Api GET '/api/activities?status=Overdue'; "$($p.totalCount) overdue" }
Check 'User lookup' { $u = Api GET '/api/users/lookup'; Assert ($u.Count -ge 3) 'expected admin, manager, rep'; ($u.fullName -join ', ') }

Write-Host "`n3. End-to-end workflow (records tagged $tag)" -ForegroundColor Cyan
$created = [ordered]@{ activities = @(); opportunities = @(); contacts = @(); leads = @(); accounts = @() }

Check 'Create account' {
    $script:account = Api POST '/api/accounts' @{ name = "$tag Contoso Pty Ltd"; industry = 'Technology'; city = 'Sydney'; country = 'Australia'; website = 'https://contoso.example'; annualRevenue = 2500000 }
    $created.accounts += $script:account.id; $script:account.name
}
Check 'Validation rejects bad input' { $s = StatusOf { Api POST '/api/accounts' @{ name = ''; email = 'not-an-email' } }; Assert ($s -eq 400) "got $s"; '400' }
Check 'Update account' {
    $u = Api PUT "/api/accounts/$($script:account.id)" @{ name = $script:account.name; industry = 'Software'; city = 'Sydney'; country = 'Australia'; phone = '+61 2 9000 0000' }
    Assert ($u.industry -eq 'Software') 'industry not updated'; $u.industry
}
Check 'Create contact on account' {
    $script:contact = Api POST '/api/contacts' @{ firstName = 'Erin'; lastName = "Tester $tag"; email = 'erin.tester@contoso.example'; jobTitle = 'CIO'; accountId = $script:account.id }
    $created.contacts += $script:contact.id
    Assert ($script:contact.accountName -eq $script:account.name) 'not linked'; $script:contact.fullName
}
Check 'Create opportunity (Proposal)' {
    $script:opp = Api POST '/api/opportunities' @{ name = "$tag Platform deal"; accountId = $script:account.id; contactId = $script:contact.id; amount = 120000; stage = 'Proposal'; expectedCloseDate = $today.AddDays(30).ToString('yyyy-MM-dd') }
    $created.opportunities += $script:opp.id
    Assert ($script:opp.probability -eq 50) "probability $($script:opp.probability)"; "probability=$($script:opp.probability)%"
}
Check 'Account shows contact and pipeline' {
    $a = Api GET "/api/accounts/$($script:account.id)"
    Assert ($a.contactCount -eq 1 -and $a.openPipelineAmount -eq 120000) "contacts=$($a.contactCount) pipeline=$($a.openPipelineAmount)"; "pipeline=$($a.openPipelineAmount)"
}
Check 'Account with deals cannot be deleted' { $s = StatusOf { Api DELETE "/api/accounts/$($script:account.id)" }; Assert ($s -eq 409) "got $s"; '409 Conflict' }
Check 'Log overdue activity' {
    $script:act = Api POST '/api/activities' @{ type = 'Call'; subject = "$tag Follow-up call"; priority = 'High'; dueAt = $today.AddDays(-1).ToUniversalTime().ToString('o'); accountId = $script:account.id; opportunityId = $script:opp.id }
    $created.activities += $script:act.id
    $o = Api GET "/api/activities?status=Overdue&opportunityId=$($script:opp.id)"
    Assert ($o.totalCount -eq 1) 'not in overdue list'; 'listed as overdue'
}
Check 'Complete activity' { $c = Api POST "/api/activities/$($script:act.id)/complete"; Assert ($null -ne $c.completedAt) 'not completed'; 'completed' }
Check 'Move deal to Closed Won' {
    $before = (Api GET '/api/dashboard').wonThisMonthAmount
    $w = Api PATCH "/api/opportunities/$($script:opp.id)/stage" @{ stage = 'ClosedWon' }
    Assert ($w.probability -eq 100 -and $w.closedAt) 'not closed'
    $after = (Api GET '/api/dashboard').wonThisMonthAmount
    Assert (($after - $before) -eq 120000) "dashboard won delta $($after - $before)"; "won this month +120000"
}
Check 'Create and convert lead' {
    $lead = Api POST '/api/leads' @{ firstName = 'Lee'; lastName = "Prospect $tag"; company = "$tag Fabrikam"; email = 'lee@fabrikam.example'; source = 'Referral'; status = 'Qualified'; estimatedValue = 45000 }
    $created.leads += $lead.id
    $r = Api POST "/api/leads/$($lead.id)/convert" @{ createOpportunity = $true; opportunityName = "$tag Fabrikam rollout" }
    $created.opportunities += $r.opportunityId; $created.contacts += $r.contactId; $created.accounts += $r.accountId
    Assert ((Api GET "/api/leads/$($lead.id)").status -eq 'Converted') 'lead not converted'
    $s = StatusOf { Api POST "/api/leads/$($lead.id)/convert" @{ createOpportunity = $false } }
    Assert ($s -eq 409) "double convert returned $s"; 'account + contact + opportunity created'
}

Write-Host "`n4. Role-based permissions (Sales Rep)" -ForegroundColor Cyan
Check "Sales rep sign-in ($RepEmail)" { $script:repToken = (Api POST '/api/auth/login' @{ email = $RepEmail; password = $password } -Token $null).accessToken; 'ok' }
Check "Rep cannot edit manager's account" { $s = StatusOf { Api PUT "/api/accounts/$($script:account.id)" @{ name = 'Hijack' } -Token $script:repToken }; Assert ($s -eq 403) "got $s"; '403' }
Check 'Rep cannot delete records' { $s = StatusOf { Api DELETE "/api/leads/$($created.leads[0])" -Token $script:repToken }; Assert ($s -eq 403) "got $s"; '403' }
Check 'Rep cannot open user admin' { $s = StatusOf { Api GET '/api/users' -Token $script:repToken }; Assert ($s -eq 403) "got $s"; '403' }
Check 'Rep can create own record' {
    $a = Api POST '/api/accounts' @{ name = "$tag Rep account" } -Token $script:repToken
    $created.accounts += $a.id; Assert ($a.ownerName -like 'Sam*') "owner $($a.ownerName)"; "owner=$($a.ownerName)"
}

if (-not $Keep) {
    Write-Host "`n5. Clean up test records" -ForegroundColor Cyan
    Check 'Delete E2E records' {
        foreach ($kind in $created.Keys) { foreach ($id in $created[$kind] | Where-Object { $_ }) { Api DELETE "/api/$kind/$id" | Out-Null } }
        $left = (Api GET "/api/accounts?search=$([uri]::EscapeDataString($tag))").totalCount
        Assert ($left -eq 0) "$left accounts remain"; 'removed'
    }
}

$adminEmail = $script:secrets.'Seed:AdminEmail'
$adminPassword = $script:secrets.'Seed:AdminPassword'
if ($adminEmail -and $adminPassword) {
    Write-Host "`n6. Platform: tenants, templates, custom objects, automation, audit (PostgreSQL)" -ForegroundColor Cyan
    $slug = "e2e-$(Get-Date -Format 'HHmmss')"
    Check 'Platform admin sign-in' {
        $script:adminToken = (Api POST '/api/auth/login' @{ email = $adminEmail; password = $adminPassword } -Token $null).accessToken
        $me = Api GET '/api/auth/me' -Token $script:adminToken
        Assert $me.isPlatformAdmin 'not a platform admin'; $me.email
    }
    Check 'Provision Help Desk tenant from template (with demo data)' {
        $script:tenant = Api POST '/api/platform/tenants' @{
            name = "E2E Help Desk $slug"; slug = $slug; template = 'helpdesk'; currency = 'USD'
            adminEmail = "admin@$slug.test"; adminFirstName = 'Helen'; adminLastName = 'Desk'; adminPassword = 'Tenant#Pass123'; seedDemoData = $true
        } -Token $script:adminToken
        $script:hdToken = (Api POST '/api/auth/login' @{ email = "admin@$slug.test"; password = 'Tenant#Pass123' } -Token $null).accessToken
        $script:tenant.slug
    }
    Check 'Tenant has its own configuration' {
        $m = Api GET '/api/metadata' -Token $script:hdToken
        $ticket = $m.objects | Where-Object key -eq 'ticket'
        Assert ($null -ne $ticket -and $ticket.hasPipeline) 'ticket object missing'
        Assert (($m.objects | Where-Object key -eq 'account').pluralLabel -eq 'Customers') 'account not relabelled'
        "ticket: $($ticket.fields.Count) fields, $($ticket.stages.Count) stages; accounts shown as Customers"
    }
    Check 'Custom-field filter uses JSONB containment' {
        $all = Api GET '/api/records/ticket?pageSize=100' -Token $script:hdToken
        $high = Api GET '/api/records/ticket?cf.priority=High&pageSize=100' -Token $script:hdToken
        Assert ($all.totalCount -eq 8) "expected 8 demo tickets, got $($all.totalCount)"
        Assert (@($high.items | Where-Object { $_.customFields.priority -ne 'High' }).Count -eq 0) 'filter leaked other priorities'
        "$($high.totalCount) of $($all.totalCount) tickets are High"
    }
    Check 'Automation: urgent ticket creates task and notification' {
        $before = (Api GET '/api/notifications' -Token $script:hdToken).unreadCount
        $t = Api POST '/api/records/ticket' @{ name = 'Production outage'; customFields = @{ priority = 'Urgent'; category = 'Incident' } } -Token $script:hdToken
        $after = (Api GET '/api/notifications' -Token $script:hdToken).unreadCount
        $tasks = Api GET "/api/activities?customRecordId=$($t.id)" -Token $script:hdToken
        Assert ($after -eq $before + 1) 'no notification'
        Assert ($tasks.totalCount -eq 1) 'no task'
        $script:ticketId = $t.id
        "task: $($tasks.items[0].subject)"
    }
    Check 'Stage change and audit history' {
        Api PATCH "/api/records/ticket/$($script:ticketId)/stage" @{ stage = 'resolved' } -Token $script:hdToken | Out-Null
        $h = Api GET "/api/audit/ticket/$($script:ticketId)" -Token $script:hdToken
        Assert (@($h | Where-Object { $_.changes.stage }).Count -ge 1) 'stage change not audited'
        "$(@($h).Count) audit entries"
    }
    Check 'Tenant isolation' {
        $defaultAccount = (Api GET '/api/accounts?pageSize=1').items[0].id
        $s = StatusOf { Api GET "/api/accounts/$defaultAccount" -Token $script:hdToken }
        Assert ($s -eq 404) "got $s"
        $s2 = StatusOf { Api GET '/api/records/ticket' }
        Assert ($s2 -eq 404) "default tenant sees tickets: $s2"; 'records invisible across tenants'
    }
    Check 'Custom field on standard object (default tenant)' {
        $a = Api POST '/api/accounts' @{ name = "$tag Tiered"; customFields = @{ tier = 'Gold' } }
        $f = Api GET '/api/accounts?cf.tier=Gold&pageSize=100'
        Api DELETE "/api/accounts/$($a.id)" | Out-Null
        Assert (@($f.items | Where-Object id -eq $a.id).Count -eq 1) 'not found by custom field'; 'filter by cf.tier works'
    }

    Check 'CSV import: dry run reports bad rows, then imports all-or-nothing' {
        $before = (Api GET '/api/records/ticket?pageSize=1' -Token $script:hdToken).totalCount
        $mapping = @{ Subject = 'name'; Priority = 'cf.priority'; Due = 'cf.due_date' }
        $bad = "Subject,Priority,Due`nPrinter offline,high,31/12/2026`nBroken row,Whenever,not-a-date`n"
        $r = Api POST '/api/import/ticket' @{ csv = $bad; mapping = $mapping; dryRun = $false } -Token $script:hdToken
        Assert ($r.created -eq 0 -and $r.errorCount -eq 1 -and $r.errors[0].row -eq 3) "expected row 3 to fail, got $($r | ConvertTo-Json -Depth 4 -Compress)"
        $good = "Subject,Priority,Due`n""Printer offline, 3rd floor"",high,31/12/2026`nVPN drops,Urgent,2026-11-01`n"
        $dry = Api POST '/api/import/ticket' @{ csv = $good; mapping = $mapping; dryRun = $true } -Token $script:hdToken
        Assert ($dry.errorCount -eq 0 -and $dry.created -eq 0) 'dry run should validate without saving'
        $done = Api POST '/api/import/ticket' @{ csv = $good; mapping = $mapping; dryRun = $false } -Token $script:hdToken
        $after = (Api GET '/api/records/ticket?pageSize=1' -Token $script:hdToken).totalCount
        Assert ($done.created -eq 2 -and $after -eq $before + 2) "created $($done.created), count $before -> $after"
        "2 tickets imported; bad file rejected at row 3"
    }
    Check 'Attachments: upload, download and delete a file on a record' {
        $ticket = (Api GET '/api/records/ticket?pageSize=1' -Token $script:hdToken).items[0]
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "smoke-$([guid]::NewGuid().ToString('N')).pdf"
        [IO.File]::WriteAllText($tmp, '%PDF-1.4 smoke test')
        try {
            $headers = @{ Authorization = "Bearer $script:hdToken" }
            $item = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/attachments?objectKey=ticket&recordId=$($ticket.id)" -Headers $headers -Form @{ file = Get-Item $tmp }
            $download = Invoke-WebRequest -Uri "$BaseUrl/api/attachments/$($item.id)/content" -Headers $headers -UseBasicParsing
            $contentType = "$($download.Headers['Content-Type'])"
            Assert ($contentType -like 'application/pdf*') "content type $contentType"
            Assert ([Text.Encoding]::ASCII.GetString($download.Content) -eq '%PDF-1.4 smoke test') 'downloaded bytes differ'
            $s = StatusOf { Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/attachments?objectKey=ticket&recordId=$($ticket.id)" -Headers $headers -Form @{ file = Get-Item $PSCommandPath } }
            Assert ($s -eq 400) ".ps1 upload returned $s"
            Api DELETE "/api/attachments/$($item.id)" -Token $script:hdToken | Out-Null
            $left = Api GET "/api/attachments?objectKey=ticket&recordId=$($ticket.id)" -Token $script:hdToken
            Assert ($left.Count -eq 0) "file not deleted ($($left.Count) left)"
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
        'round trip ok; script upload refused'
    }
    Check 'Password reset requests never reveal whether an account exists' {
        $known = (Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/auth/forgot-password" -ContentType 'application/json' -Body (@{ email = "admin@$slug.test" } | ConvertTo-Json) -UseBasicParsing).StatusCode
        $unknown = (Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/auth/forgot-password" -ContentType 'application/json' -Body (@{ email = "nobody-$slug@example.test" } | ConvertTo-Json) -UseBasicParsing).StatusCode
        Assert ($known -eq 202 -and $unknown -eq 202) "known $known, unknown $unknown"
        '202 for both'
    }

    # Self-registration: global platform settings are changed temporarily and always restored below.
    $regEmail = "joiner@$slug.test"
    Check 'Self-registration needs both the platform switch and the organisation opt-in' {
        $script:originalPlatform = (Api GET '/api/platform/settings' -Token $script:adminToken).settings
        Assert (-not (Api GET "/api/auth/registration-options?tenant=$slug" -Token $null).available) 'available before the organisation opted in'
        $temp = $script:originalPlatform | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $temp.selfRegistrationEnabled = $true; $temp.emailVerificationEnabled = $false; $temp.smsVerificationEnabled = $false
        Api PUT '/api/platform/settings' $temp -Token $script:adminToken | Out-Null
        Assert (-not (Api GET "/api/auth/registration-options?tenant=$slug" -Token $null).available) 'available without the organisation opt-in'
        $org = Api PUT '/api/settings' @{ name = $script:tenant.name; currency = 'USD'; selfRegistrationEnabled = $true } -Token $script:hdToken
        Assert ($org.selfRegistrationEnabled -and $org.selfRegistrationAllowedByPlatform) 'organisation setting not saved'
        $o = Api GET "/api/auth/registration-options?tenant=$slug" -Token $null
        Assert ($o.available -and $o.tenantName -eq $script:tenant.name) 'not available after opt-in'
        "open for $slug"
    }
    Check 'Registered user cannot sign in until approved; admins are notified' {
        $before = (Api GET '/api/notifications' -Token $script:hdToken).unreadCount
        $r = Api POST '/api/auth/register' @{ tenantCode = $slug; email = $regEmail; firstName = 'Jo'; lastName = 'Iner'; phoneNumber = '+61 412 345 678'; password = 'Joiner#Pass123' } -Token $null
        Assert (@($r.verification).Count -eq 0) 'unexpected verification step'
        $s = StatusOf { Api POST '/api/auth/login' @{ email = $regEmail; password = 'Joiner#Pass123' } -Token $null }
        Assert ($s -eq 403) "pending login returned $s"
        Assert ((Api GET '/api/notifications' -Token $script:hdToken).unreadCount -eq $before + 1) 'admin not notified'
        'login 403 while pending approval'
    }
    Check 'Admin approves with a role; user can sign in' {
        $users = Api GET '/api/users?status=PendingApproval' -Token $script:hdToken
        $pending = $users | Where-Object email -eq $regEmail
        Assert ($null -ne $pending -and -not $pending.isActive) 'not listed as pending'
        $s = StatusOf { Api POST "/api/users/$($pending.id)/approve" @{ roleIds = @() } -Token $script:hdToken }
        Assert ($s -eq 400) "approval without a role returned $s"
        $roles = Api GET '/api/admin/roles' -Token $script:hdToken
        $role = $roles | Where-Object name -ne 'Administrator' | Select-Object -First 1
        $u = Api POST "/api/users/$($pending.id)/approve" @{ roleIds = @($role.id) } -Token $script:hdToken
        Assert ($u.isActive -and $u.registrationStatus -eq 'Approved') 'not approved'
        $login = Api POST '/api/auth/login' @{ email = $regEmail; password = 'Joiner#Pass123' } -Token $null
        Assert ($login.user.roles -contains $role.name) 'role not granted'
        "signed in as $($role.name)"
    }
    Check 'Restore platform settings' {
        Assert ($null -ne $script:originalPlatform) 'original settings were not captured'
        Api PUT '/api/platform/settings' $script:originalPlatform -Token $script:adminToken | Out-Null
        'restored'
    }

    # Recruitment (ATS) tenant: candidates, applications per job, unique emails, pipeline per job.
    $atsSlug = "$slug-ats"
    Check 'Provision Recruitment tenant (template v2 with demo data)' {
        $script:atsTenant = Api POST '/api/platform/tenants' @{
            name = "E2E Recruiting $slug"; slug = $atsSlug; template = 'recruitment'; currency = 'USD'
            adminEmail = "admin@$atsSlug.test"; adminFirstName = 'Rita'; adminLastName = 'Cruiter'; adminPassword = 'Tenant#Pass123'; seedDemoData = $true
        } -Token $script:adminToken
        $script:atsToken = (Api POST '/api/auth/login' @{ email = "admin@$atsSlug.test"; password = 'Tenant#Pass123' } -Token $null).accessToken
        $m = Api GET '/api/metadata' -Token $script:atsToken
        $app = $m.objects | Where-Object key -eq 'application'
        Assert ($null -ne ($m.objects | Where-Object key -eq 'candidate')) 'candidate object missing'
        Assert (($app.fields | Where-Object key -eq 'candidate').lookupObjectKey -eq 'candidate') 'application not linked to candidate'
        $counts = foreach ($k in 'position', 'candidate', 'application') { (Api GET "/api/records/$k`?pageSize=1" -Token $script:atsToken).totalCount }
        Assert ($counts[0] -gt 0 -and $counts[1] -gt 0 -and $counts[2] -gt 0) "demo data missing: $($counts -join '/')"
        "$($counts[0]) jobs, $($counts[1]) candidates, $($counts[2]) applications"
    }
    Check 'Candidate email is unique (forms and import)' {
        $c = Api POST '/api/records/candidate' @{ name = 'Smoke Candidate'; customFields = @{ email = "Smoke.Candidate@$atsSlug.test" } } -Token $script:atsToken
        $script:candidateId = $c.id
        Assert ($c.customFields.email -eq "smoke.candidate@$atsSlug.test") "email not normalised: $($c.customFields.email)"
        $s = StatusOf { Api POST '/api/records/candidate' @{ name = 'Duplicate'; customFields = @{ email = "SMOKE.candidate@$atsSlug.test" } } -Token $script:atsToken }
        Assert ($s -eq 400) "duplicate email returned $s"
        $csv = "Name,Email`nA One,dup@$atsSlug.test`nA Two,DUP@$atsSlug.test`n"
        $r = Api POST '/api/import/candidate' @{ csv = $csv; mapping = @{ Name = 'name'; Email = 'cf.email' }; dryRun = $true } -Token $script:atsToken
        Assert ($r.errorCount -eq 1 -and $r.errors[0].row -eq 3) "in-file duplicate not caught: $($r | ConvertTo-Json -Depth 4 -Compress)"
        'duplicate rejected (400); import flags row 3'
    }
    Check 'Pipeline per job: board and list filter by the linked job' {
        $job = (Api GET '/api/records/position?pageSize=1' -Token $script:atsToken).items[0]
        $a = Api POST '/api/records/application' @{ name = 'Smoke Candidate'; customFields = @{ candidate = $script:candidateId; position = $job.id } } -Token $script:atsToken
        $board = Api GET "/api/records/application/board?cf.position=$($job.id)" -Token $script:atsToken
        $cards = @($board | ForEach-Object { $_ })
        Assert (@($cards | Where-Object id -eq $a.id).Count -eq 1) 'new application not on the job board'
        Assert (@($cards | Where-Object { $_.customFields.position -ne $job.id }).Count -eq 0) 'board shows other jobs'
        $mine = Api GET "/api/records/application?cf.candidate=$($script:candidateId)" -Token $script:atsToken
        Assert ($mine.totalCount -eq 1) "candidate has $($mine.totalCount) applications"
        "$($cards.Count) cards for '$($job.name)'"
    }
    if (-not $Keep) {
        Check 'Delete the recruitment tenant' {
            Api PUT "/api/platform/tenants/$($script:atsTenant.id)" @{ name = $script:atsTenant.name; currency = 'USD'; status = 'Suspended' } -Token $script:adminToken | Out-Null
            Api DELETE "/api/platform/tenants/$($script:atsTenant.id)" -Token $script:adminToken | Out-Null
            'deleted'
        }
    }
    if (-not $Keep) {
        Check 'Suspend and delete the test tenant' {
            Api PUT "/api/platform/tenants/$($script:tenant.id)" @{ name = $script:tenant.name; currency = 'USD'; status = 'Suspended' } -Token $script:adminToken | Out-Null
            $s = StatusOf { Api POST '/api/auth/login' @{ email = "admin@$slug.test"; password = 'Tenant#Pass123' } -Token $null }
            Assert ($s -eq 403) "suspended login returned $s"
            Api DELETE "/api/platform/tenants/$($script:tenant.id)" -Token $script:adminToken | Out-Null
            'suspended (login blocked) and deleted'
        }
    }
} else {
    Write-Host "`n6. Platform checks skipped (Seed:AdminEmail / Seed:AdminPassword not in user-secrets)" -ForegroundColor Yellow
}

Write-Host "`nResult: $script:passed passed, $script:failed failed`n" -ForegroundColor ($script:failed ? 'Red' : 'Green')
exit $script:failed
