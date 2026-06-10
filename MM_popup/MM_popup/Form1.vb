'    Copyright (c) 2011, 2012 Felix Belzile
'    Official software website: http://monkmode.local
'    Contact: felixbelzile@rogers.com  Web: http://felixbelzile.com

'    This file is part of MonkMode
'
'    MonkMode is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    MonkMode is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with MonkMode.  If not, see <http://www.gnu.org/licenses/>.

Public Class Form1

    Private Sub Button1_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles Button1.Click
        End
    End Sub

    Private Sub Button2_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles Button2.Click

        Try
            Process.Start(Application.StartupPath & "\MonkMode.exe")
            End
        Catch ex As Exception
            MsgBox("Could not locate MonkMode's main executable.")
            MsgBox(ex.Message)
        End Try
        End
    End Sub

    Private Sub Form1_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load

    End Sub
End Class
