Get-ChildItem -Path 'USPSystem' -Recurse -Filter '*.cs' | ForEach-Object {  = Get-Content .FullName -Raw;  =  -replace 'USPEducation\.', 'USPSystem.'; Set-Content .FullName  }
