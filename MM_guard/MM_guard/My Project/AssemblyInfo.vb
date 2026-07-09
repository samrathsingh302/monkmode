'    Copyright (C) 2026 Samrath Singh
'
'    This file is part of MonkMode, a fork of Cold Turkey.
'    Source: https://github.com/samrathsingh302/monkmode
'
'    This program is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    This program is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with this program.  If not, see <https://www.gnu.org/licenses/>.

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
