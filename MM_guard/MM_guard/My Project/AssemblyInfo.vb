Imports System.Resources

Imports System
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices

' Let the unit-test project see Friend members (the Guardian gates + consts).
<Assembly: InternalsVisibleTo("MonkMode.Tests")>

<Assembly: AssemblyTitle("MonkMode Guardian")>
<Assembly: AssemblyDescription("MonkMode watchdog guardian (B1 layer 2)")>
<Assembly: AssemblyCompany("")>
<Assembly: AssemblyProduct("")>
<Assembly: AssemblyCopyright("Copyright © Felix Belzile 2012")>
<Assembly: AssemblyTrademark("Beta")>

<Assembly: ComVisible(False)>

'The following GUID is for the ID of the typelib if this project is exposed to COM
<Assembly: Guid("8674baa9-b017-41ee-9223-55e9bd57a25f")>

<Assembly: AssemblyVersion("0.7.0.0")>
<Assembly: AssemblyFileVersion("0.7.0.0")>

<Assembly: NeutralResourcesLanguageAttribute("en")>
