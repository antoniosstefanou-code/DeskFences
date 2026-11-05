# Χτίζει το DeskFences.exe με τον C# compiler που υπάρχει ήδη στα Windows (.NET Framework 4.x).
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /codepage:65001 /optimize+ /out:"$PSScriptRoot\DeskFences.exe" `
    /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$PSScriptRoot\DeskFences.cs"
