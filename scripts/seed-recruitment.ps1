<#
.SYNOPSIS
  Seeds realistic recruitment demo data (job openings, candidates, applications, interviews, scorecards and offers) into a tenant via the API.
.DESCRIPTION
  Signs in as a tenant administrator and creates records through the normal API, so all validation, automation and
  permissions apply. Safe to re-run: candidates are matched by email and existing ones are reused, and it won't
  create a duplicate application for the same candidate and job, or interviews or offers for an application that has some.
  Records are owned by the signed-in admin, who is also the interviewer.
.EXAMPLE
  ./scripts/seed-recruitment.ps1 -Email admin@hardeeporg.example
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5290',
    [Parameter(Mandatory)][string]$Email,
    [string]$Password
)

$ErrorActionPreference = 'Stop'

function Api([string]$Method, [string]$Path, $Body = $null) {
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; ContentType = 'application/json'; Headers = @{ Authorization = "Bearer $script:token" } }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 8) }
    Invoke-RestMethod @params
}
function Pick($items) { $items[(Get-Random -Maximum $items.Count)] }

# Password is typed into the terminal, never passed through a parameter unless given.
if (-not $Password) {
    $secure = Read-Host -AsSecureString "Password for $Email"
    $Password = [Net.NetworkCredential]::new('', $secure).Password
}
$script:token = (Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' `
        -Body (@{ email = $Email; password = $Password } | ConvertTo-Json)).accessToken
Write-Host "Signed in as $Email" -ForegroundColor Green

$meta = Api GET '/api/metadata'
foreach ($key in 'position', 'candidate', 'application') {
    if (-not ($meta.objects | Where-Object key -eq $key)) { throw "This organisation has no '$key' object. Is it a Recruitment tenant?" }
}
$stages = @(($meta.objects | Where-Object key -eq 'application').stages | ForEach-Object { $_.key })
function Options([string]$object, [string]$field) {
    @(($meta.objects | Where-Object key -eq $object).fields | Where-Object key -eq $field | ForEach-Object { $_.options } | ForEach-Object { $_ })
}
$skillOptions = Options 'candidate' 'skills'
$candidateSources = Options 'candidate' 'source'
$applicationSources = Options 'application' 'source'
$today = Get-Date

# ---- Job openings (first four published to the careers page) ----
$jobDefs = @(
    @{ t = 'Senior Backend Engineer'; d = 'Engineering'; l = 'Sydney'; e = 'Full-time'; o = 2; s = 165000 }
    @{ t = 'Product Designer'; d = 'Design'; l = 'Melbourne'; e = 'Full-time'; o = 1; s = 130000 }
    @{ t = 'Data Analyst'; d = 'Analytics'; l = 'Remote'; e = 'Contract'; o = 1; s = 110000 }
    @{ t = 'Customer Success Lead'; d = 'Customer Success'; l = 'Brisbane'; e = 'Full-time'; o = 1; s = 120000 }
    @{ t = 'DevOps Engineer'; d = 'Engineering'; l = 'Sydney'; e = 'Full-time'; o = 1; s = 155000 }
    @{ t = 'Graduate Accountant'; d = 'Finance'; l = 'Perth'; e = 'Full-time'; o = 3; s = 75000 }
)
$existingJobs = @((Api GET '/api/records/position?pageSize=200').items)
$jobs = @()
$i = 0
foreach ($j in $jobDefs) {
    $found = $existingJobs | Where-Object name -eq $j.t | Select-Object -First 1
    if (-not $found) {
        $found = Api POST '/api/records/position' @{
            name         = $j.t
            customFields = @{
                department   = $j.d; location = $j.l; employment_type = $j.e
                status       = $(if ($i -eq $jobDefs.Count - 1) { 'On hold' } else { 'Open' })
                openings     = $j.o; salary_max = $j.s
                closing_date = $today.AddDays(14 + 7 * $i).ToString('yyyy-MM-dd')
                publish      = ($i -lt 4)
                description  = "We're looking for a $($j.t) to join our $($j.d) team ($($j.l), $($j.e.ToLower())).`n`n" +
                               "You'll work with a friendly, experienced team on meaningful projects, with flexible hours and support to keep learning."
            }
        }
    }
    $jobs += $found
    $i++
}
Write-Host "Job openings: $($jobs.Count) (4 published to the careers page)" -ForegroundColor Cyan

# ---- Candidates (unique emails; existing ones are reused) ----
$fn = 'Ava', 'Liam', 'Mia', 'Noah', 'Zoe', 'Ethan', 'Aria', 'Lucas', 'Ivy', 'Kai', 'Ruby', 'Omar', 'Nina', 'Raj', 'Grace', 'Leo', 'Sara', 'Max'
$ln = 'Nguyen', 'Patel', 'Smith', 'Chen', 'Khan', 'Brown', 'Singh', 'Garcia', 'Wong', 'Taylor', 'Ali', 'Murphy', 'Haddad', 'Lee', 'Rossi', 'Walker'
$titles = 'Software Engineer', 'Senior Developer', 'UX Designer', 'Business Analyst', 'Data Scientist', 'Account Manager', 'Accountant', 'Site Reliability Engineer'
$companies = 'Northwind', 'Contoso', 'Fabrikam', 'Acme', 'Globex', 'Initech', 'Umbrella', 'Hooli'
$cities = 'Sydney', 'Melbourne', 'Brisbane', 'Perth', 'Adelaide', 'Remote'
$notice = 'Immediate', '2 weeks', '4 weeks', '1 month'

$candidates = @()
for ($n = 0; $n -lt $fn.Count; $n++) {
    $last = $ln[($n * 7 + 3) % $ln.Count]
    $email = ("{0}.{1}@example.com" -f $fn[$n], $last).ToLower()
    $existing = @((Api GET "/api/records/candidate?cf.email=$email").items)
    if ($existing.Count -gt 0) { $candidates += $existing[0]; continue }
    $candidates += Api POST '/api/records/candidate' @{
        name         = "$($fn[$n]) $last"
        customFields = @{
            email           = $email
            phone           = ('+61 4{0:00} {1:000} {2:000}' -f (Get-Random -Max 100), (Get-Random -Max 1000), (Get-Random -Max 1000))
            location        = (Pick $cities); current_title = (Pick $titles); current_company = (Pick $companies)
            skills          = @($skillOptions | Get-Random -Count ([Math]::Min(3, [Math]::Max(1, $skillOptions.Count))))
            source          = (Pick $candidateSources)
            linkedin        = "https://www.linkedin.com/in/$($fn[$n].ToLower())-$($last.ToLower())"
            expected_salary = ((Get-Random -Minimum 80 -Maximum 180) * 1000)
            notice_period   = (Pick $notice)
            summary         = "$((Pick $titles)) with experience across $((Pick $companies)) and $((Pick $companies))."
        }
    }
}
Write-Host "Candidates: $($candidates.Count)" -ForegroundColor Cyan

# ---- Applications: unique candidate x job pairs, spread across the funnel ----
$distribution = @(@('applied') * 8 + @('screening') * 6 + @('interview') * 5 + @('offer') * 2 + @('hired') * 1 + @('rejected') * 4 | Where-Object { $_ -in $stages })
$seen = [System.Collections.Generic.HashSet[string]]::new()
foreach ($a in @((Api GET '/api/records/application?pageSize=500').items)) { [void]$seen.Add("$($a.customFields.candidate)|$($a.customFields.position)") }

$made = 0
foreach ($stage in $distribution) {
    $attempts = 0
    do {
        $cand = Pick $candidates; $job = Pick $jobs; $key = "$($cand.id)|$($job.id)"; $attempts++
    } while ($seen.Contains($key) -and $attempts -lt 25)
    if ($seen.Contains($key)) { continue }
    [void]$seen.Add($key)

    $cf = @{ candidate = $cand.id; position = $job.id; source = (Pick $applicationSources); cover_note = "Keen to join as $($job.name)." }
    if ($stage -in 'interview', 'offer', 'hired') { $cf.rating = (Get-Random -Minimum 3 -Maximum 6) }
    if ($stage -eq 'interview') { $cf.interview_date = $today.AddDays((Get-Random -Minimum 1 -Maximum 10)).ToString('yyyy-MM-ddTHH:mm:ssZ') }
    if ($stage -in 'offer', 'hired', 'rejected') { $cf.feedback = 'Strong technically; good team fit.' }
    Api POST '/api/records/application' @{ name = $cand.name; stage = $stage; customFields = $cf } | Out-Null
    $made++
}
Write-Host "Applications: $made new (total $($seen.Count))" -ForegroundColor Cyan

# ---- Interviews: a completed one with your scorecard past screening, plus an upcoming one in Interview ----
$me = Api GET '/api/auth/me'
$criteria = @($meta.picklists.'interview.criteria' | Where-Object { $_ })
$feedback = 'Clear communicator with solid fundamentals.', 'Strong problem solving; would like more depth on design.', 'Great culture fit and very motivated.'
$interviews = 0
foreach ($app in @((Api GET '/api/records/application?pageSize=500').items | Where-Object stage -in 'interview', 'offer', 'hired')) {
    if (@(Api GET "/api/interviews?applicationId=$($app.id)").Count -gt 0) { continue }
    $held = (Api POST '/api/interviews' @{
            applicationId = $app.id; scheduledAt = $today.Date.AddDays(-(Get-Random -Minimum 2 -Maximum 20)).AddHours(10).ToUniversalTime().ToString('o')
            durationMinutes = 45; kind = 'Video'; location = 'https://meet.example.com/first-round'; notes = 'First-round interview.'
            interviewerIds = @($me.id); emailCandidate = $false
        }).interview
    $ratings = @{}
    foreach ($c in $criteria) { $ratings[$c] = Get-Random -Minimum 3 -Maximum 6 }
    Api PUT "/api/interviews/$($held.id)/scorecard" @{
        recommendation = $(if ($app.stage -eq 'interview') { 'Yes' } else { 'StrongYes' }); ratings = $ratings; notes = (Pick $feedback)
    } | Out-Null
    Api POST "/api/interviews/$($held.id)/complete" | Out-Null
    $interviews++
    if ($app.stage -ne 'interview') { continue }
    Api POST '/api/interviews' @{
        applicationId = $app.id; scheduledAt = $today.Date.AddDays((Get-Random -Minimum 1 -Maximum 8)).AddHours((Get-Random -Minimum 9 -Maximum 16)).ToUniversalTime().ToString('o')
        durationMinutes = 60; kind = 'Onsite'; location = 'Head office, level 3'; notes = 'Panel interview with the hiring manager.'
        interviewerIds = @($me.id); emailCandidate = $false
    } | Out-Null
    $interviews++
}
Write-Host "Interviews: $interviews new (with your scorecards on the completed ones)" -ForegroundColor Cyan

# ---- Offers: open for applications at Offer; accepted (through the candidate's link) for hires ----
$offers = 0
foreach ($app in @((Api GET '/api/records/application?pageSize=500').items | Where-Object stage -in 'offer', 'hired')) {
    if (@(Api GET "/api/offers?applicationId=$($app.id)").Count -gt 0) { continue }
    $d = Api GET "/api/offers/defaults?applicationId=$($app.id)"
    $draft = Api POST '/api/offers' @{
        applicationId = $app.id; jobTitle = $(if ($d.jobTitle) { $d.jobTitle } else { 'Team member' })
        salary = $(if ($d.salary) { $d.salary } else { (Get-Random -Minimum 80 -Maximum 170) * 1000 }); currency = $d.currency; payPeriod = 'Year'
        startDate = $d.startDate; expiresAt = $d.expiresAt; letter = $d.letter; notes = 'Demo offer'
    }
    $sent = Api POST "/api/offers/$($draft.id)/send"
    if ($app.stage -eq 'hired') {
        $token = $sent.link.Substring($sent.link.LastIndexOf('/') + 1)
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/offer-response/$token/accept" -ContentType 'application/json' `
            -Body (@{ signedName = $app.name; agree = $true } | ConvertTo-Json) | Out-Null
    }
    $offers++
}
Write-Host "Offers: $offers new (accepted for hires, awaiting a reply at Offer)" -ForegroundColor Cyan
Write-Host "`nDone. Open Applications, My interviews and Hiring analytics in the app to see the data." -ForegroundColor Green
