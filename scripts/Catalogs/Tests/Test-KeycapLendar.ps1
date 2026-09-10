function Test-KeycapLendar {
    Test-Case 'KeycapLendar preserves names and credits without confusing brands with profiles' {
        $docs = (Read-CatalogJson "$PSScriptRoot/Fixtures/keycaplendar.json").documents
        $a = Convert-KeycapLendarDocument $docs[0]
        $b = Convert-KeycapLendarDocument $docs[1]
        $c = Convert-KeycapLendarDocument $docs[2]
        Assert ($a.entry.name -ceq 'GMK Calendar Explorer++ R2 Accent Kit' -and -not $a.excluded) 'Round, plus signs or add-on lost.'
        Assert ($a.entry.designer -ceq 'Alice & Bob' -and $a.url.EndsWith('?keysetAlias=shareA')) 'Credits or share link lost.'
        Assert ($a.notes.Contains('gbLaunch: 2026-09-01') -and $a.notes.Contains('https://example.com/project')) 'Review dates or reference link lost.'
        Assert (-not $a.entry.Contains('manufacturer') -and -not $a.entry.Contains('brand') -and -not $a.entry.Contains('profile')) 'Mixed category became speculative metadata.'
        Assert ($b.entry.profile -ceq 'MT3' -and $b.entry.material -ceq 'PBT' -and $b.entry.name.EndsWith('CANCELED')) 'Explicit material, profile or cancellation label lost.'
        Assert (-not $b.entry.Contains('designer') -and $b.url.EndsWith('?keysetId=fixtureB')) 'Unknown credit retained or document-ID link lost.'
        Assert ($c.entry.material -ceq 'ABS' -and -not $c.entry.Contains('profile') -and -not $c.entry.Contains('designer')) 'Brand or empty credits misread.'
        $docs[0].fields.colorway.stringValue = 'Renamed Calendar Set'
        Assert ((Convert-KeycapLendarDocument $docs[0]).sourceId -ceq $a.sourceId) 'Rename changed source identity.'
        $docs[0].fields.profile.stringValue = 'Unfamiliar Category'
        Assert (-not (Convert-KeycapLendarDocument $docs[0]).entry.Contains('profile')) 'Unknown category invented a profile.'
        Assert ((Get-CatalogMatchName ('ePBT ' + [char]0x56CD + ' Because of Love')) -ne (Get-CatalogMatchName ('ePBT ' + [char]0x7121 + ' WUYINZHUYIN'))) 'Distinct CJK colorways collapsed to their brand.'
    }
    Test-Case 'KeycapLendar rejects malformed typed fields and repeated source IDs' {
        $data = Read-CatalogJson "$PSScriptRoot/Fixtures/keycaplendar.json"
        $doc = Copy-Value $data.documents[0]
        $doc.fields.Remove('colorway')
        Assert-Throws { Convert-KeycapLendarDocument $doc } 'missing colorway'
        $doc = Copy-Value $data.documents[0]
        $doc.fields.profile = @{ integerValue = '1' }
        Assert-Throws { Convert-KeycapLendarDocument $doc } 'string field'
        $doc.fields.profile = @{ stringValue = 'GMK' }
        $doc.fields.designer = @{ stringValue = 'Alice' }
        Assert-Throws { Convert-KeycapLendarDocument $doc } 'designer array'
        $doc = Copy-Value $data.documents[0]
        $doc.name = $doc.name.Replace('/keysets/', '/users/')
        Assert-Throws { Convert-KeycapLendarDocument $doc } 'document path'
        $path = Join-Path $testRoot 'keycaplendar-repeated.json'
        $data.documents += $data.documents[0]
        Write-CatalogJson $path $data
        Assert-Throws { Read-KeycapLendarPage $path ([Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)) } 'Repeated KeycapLendar document'
    }
    Test-Case 'KeycapLendar fetch follows tokens and replay requires the complete checksummed chain' {
        $data = Read-CatalogJson "$PSScriptRoot/Fixtures/keycaplendar.json"
        $fixturePageToken = 'caseSensitive+/=Token'
        function Get-CatalogResponse([string]$Url, [string]$OutFile) {
            if ($Url -ceq (Get-KeycapLendarPageUrl)) { Write-CatalogJson $OutFile @{ documents = @($data.documents[0]); nextPageToken = $fixturePageToken } }
            elseif ($Url -ceq (Get-KeycapLendarPageUrl $fixturePageToken)) { Write-CatalogJson $OutFile @{ documents = @($data.documents[1], $data.documents[2]) } }
            else { throw 'Unexpected KeycapLendar request' }
        }
        Assert ((Get-KeycapLendarPageUrl $fixturePageToken).EndsWith('pageToken=caseSensitive%2B%2F%3DToken')) 'Page token was not encoded.'
        $run = Join-Path $testRoot 'keycaplendar-fetch'
        $snapshot = Save-KeycapLendarFetch $run
        Assert ($snapshot.files.Count -eq 2 -and (Read-KeycapLendarFetch $run $snapshot.files).Count -eq 3) 'Paginated fetch failed offline replay.'
        Assert-Throws { Read-KeycapLendarFetch $run @($snapshot.files[0]) } 'incomplete'
        Assert-Throws { Read-KeycapLendarFetch $run @($snapshot.files[1], $snapshot.files[0]) } 'URL mismatch'
        Assert-Throws { Read-KeycapLendarFetch $run @($snapshot.files[0], $snapshot.files[1], $snapshot.files[1]) } 'after terminal'
        [IO.File]::AppendAllText((Join-Path $run 'raw/keycaplendar/page-1.json'), ' ')
        Assert-Throws { Read-KeycapLendarFetch $run $snapshot.files } 'checksum mismatch'
        function Get-CatalogResponse([string]$Url, [string]$OutFile) {
            $doc = if ($Url -ceq (Get-KeycapLendarPageUrl)) { $data.documents[0] } else { $data.documents[1] }
            Write-CatalogJson $OutFile @{ documents = @($doc); nextPageToken = $fixturePageToken }
        }
        Assert-Throws { Save-KeycapLendarFetch (Join-Path $testRoot 'keycaplendar-loop') } 'Repeated KeycapLendar page token'
        function Get-CatalogResponse([string]$Url, [string]$OutFile) { Write-CatalogJson $OutFile @{ documents = @() } }
        Assert-Throws { Save-KeycapLendarFetch (Join-Path $testRoot 'keycaplendar-empty') } 'Empty or incomplete'
    }
    Test-Case 'KeycapLendar ambiguous repeats require separate reviewed bindings' {
        $doc = (Read-CatalogJson "$PSScriptRoot/Fixtures/keycaplendar.json").documents[0]
        $a = Convert-KeycapLendarDocument $doc
        $doc.name = $doc.name.Replace('fixtureA', 'repeatA')
        $doc.fields.gbLaunch.stringValue = '2027-01-01'
        $b = Convert-KeycapLendarDocument $doc
        $state = Read-CatalogState (Join-Path $testRoot 'empty-keycaplendar')
        $next = New-CatalogCandidate $state (@(New-TestRecords) + @($a, $b))
        Assert ($next.report.duplicates.Count -eq 1) 'Repeated name silently merged.'
        $state.overrides.bindings['keycaplendar:fixtureA'] = 'calendar-explorer-2026'
        $state.overrides.bindings['keycaplendar:repeatA'] = 'calendar-explorer-2027'
        $next = New-CatalogCandidate $state (@(New-TestRecords) + @($a, $b))
        Assert ($next.report.duplicates.Count -eq 0 -and @($next.state.bindings | Where-Object source -eq 'keycaplendar').Count -eq 2) 'Separate reviewed IDs failed.'
    }
}
