﻿Imports System.DirectoryServices
Imports System.IO
Imports System.Security.Principal
Imports System.Threading
Imports Display_Driver_Uninstaller.Win32
Imports Microsoft.Win32
Imports WinForm = System.Windows.Forms
Imports System.Runtime


Public Class GPUCleanup

	Dim CleanupEngine As New CleanupEngine
	Dim FileIO As New FileIO
	Dim winxp As Boolean = frmMain.winxp
	Dim win10 As Boolean = frmMain.win10
	Dim win8higher As Boolean = frmMain.win8higher
	Dim sysdrv As String = Application.Paths.SystemDrive
	Dim donotremoveamdhdaudiobusfiles As Boolean = frmMain.donotremoveamdhdaudiobusfiles
	Dim objAuto As AutoResetEvent = New AutoResetEvent(False)
	Dim timer As System.Timers.Timer = New System.Timers.Timer



	Public Sub Start(ByVal config As ThreadSettings)
		Dim Array As String()
		Dim VendCHIDGPU As String = ""
		Dim vendidexpected As String = ""
		Dim VendidSC As String() = Nothing
		AddHandler timer.Elapsed, New System.Timers.ElapsedEventHandler(AddressOf TimerElapsed)

		'kill processes that read GPU stats, like RTSS, MSI Afterburner, EVGA Prec X to prevent invalid readings

		KillProcess()
		'this shouldn't be slow, so it isn't on a thread/background worker

		Select Case config.SelectedGPU
			Case GPUVendor.Nvidia : vendidexpected = "VEN_10DE" : VendCHIDGPU = "VEN_10DE&CC_03" : VendidSC = {"VEN_10DE"}
			Case GPUVendor.AMD : vendidexpected = "VEN_1002" : VendCHIDGPU = "VEN_1002&CC_03" : VendidSC = {"VEN_1002"}
			Case GPUVendor.Intel : vendidexpected = "VEN_8086" : VendCHIDGPU = "VEN_8086&CC_03" : VendidSC = {"VEN8086_MSDK", "VEN8086_GFXUI"}
			Case GPUVendor.None : vendidexpected = "NONE"
		End Select

		If vendidexpected = "NONE" Then
			Application.Log.AddWarningMessage("VendID is NONE, this is unexpected, cleaning aborted.")
			Exit Sub
		End If

		UpdateTextMethod(UpdateTextTranslated(20) + " " & config.SelectedGPU.ToString() & " " + UpdateTextTranslated(21))
		Application.Log.AddMessage("Uninstalling " + config.SelectedGPU.ToString() + " driver ...")
		UpdateTextMethod(UpdateTextTranslated(22))

		ImpersonateLoggedOnUser.ReleaseToken()

		'SpeedUP the removal of the NVIDIA adapter due to how the NVIDIA installer work.
		'Also fix a possible permission problem when removing the driver via SetupAPI
		If config.SelectedGPU = GPUVendor.Nvidia Then
			'Temporarynvidiaspeedup(config)
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "software\nvidia corporation", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames
						If IsNullOrWhitespace(child) Then Continue For
						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
							'This is not a typo, it is only to trigger the persmission check on the previous line.
						End Using
					Next
				End If
			End Using
		End If


		'------------------------------------------------------------------------------------
		' Removing the Audio associated with the GPU + AudioEndpoint+SoftwareComponent(DCH)--
		'------------------------------------------------------------------------------------
		Try
			UpdateTextMethod(UpdateTextTranslated(24))
			Application.Log.AddMessage("Executing SetupAPI: Remove Audio controler associated to the GPU(s).")
			Dim AudioDevices As List(Of SetupAPI.Device) = SetupAPI.GetDevices("media", vendidexpected, False, True)
			If AudioDevices.Count > 0 Then
				For Each AudioDevice As SetupAPI.Device In AudioDevices
					If AudioDevice IsNot Nothing Then
						'Removing Audio endpoints

						Dim Audioendpoints As List(Of SetupAPI.Device) = SetupAPI.GetDevices("audioendpoint", Nothing, False, True)
						If Audioendpoints.Count > 0 Then
							For Each Audioendpoint As SetupAPI.Device In Audioendpoints
								If Audioendpoint IsNot Nothing Then
									For Each Parent As SetupAPI.Device In Audioendpoint.ParentDevices
										If Parent IsNot Nothing Then
											If StrContainsAny(Parent.DeviceID, True, AudioDevice.DeviceID) Then
												SetupAPI.UninstallDevice(Audioendpoint) 'Removing the audioenpoint associated with the device we are trying to remove.
											End If
										End If
									Next
								End If
							Next
							Audioendpoints.Clear()
						End If


						'Removing Software components (DCH stuff, win10+)
						If win10 Then
							Application.Log.AddMessage("Executing SetupAPI: Remove SoftwareComponent.")
							Dim SoftwareComponents As List(Of SetupAPI.Device) = SetupAPI.GetDevices("SoftwareComponent", Nothing, False, True)
							If SoftwareComponents.Count > 0 Then
								For Each SoftwareComponent As SetupAPI.Device In SoftwareComponents
									If SoftwareComponent IsNot Nothing Then
										For Each Parent As SetupAPI.Device In SoftwareComponent.ParentDevices
											If Parent IsNot Nothing Then
												If StrContainsAny(Parent.DeviceID, True, AudioDevice.DeviceID) Then
													SetupAPI.UninstallDevice(SoftwareComponent)
												End If
											End If
										Next
									End If
								Next
								SoftwareComponents.Clear()
							End If
							Application.Log.AddMessage("SetupAPI: Remove SoftwareComponent Complete.")
						End If

						SetupAPI.UninstallDevice(AudioDevice) 'Removing the audio card

						If config.RemoveAudioBus Then
							For Each Parent As SetupAPI.Device In AudioDevice.ParentDevices
								If Parent IsNot Nothing Then
									SetupAPI.UninstallDevice(Parent) 'Removing the Audio bus.
								End If
							Next
						End If
					End If
				Next

				AudioDevices.Clear()
			End If
			UpdateTextMethod(UpdateTextTranslated(25))
			Application.Log.AddMessage("SetupAPI: Remove Audio controler Complete.")
		Catch ex As Exception
			'MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error)
			Application.Log.AddException(ex)
		End Try




		'Verification is there is still an AMD HD Audio Bus device and set donotremoveamdhdaudiobusfiles to true if thats the case
		Try
			donotremoveamdhdaudiobusfiles = False
			Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\CurrentControlSet\Enum\PCI")
				If subregkey IsNot Nothing Then
					For Each child2 As String In subregkey.GetSubKeyNames()
						If IsNullOrWhitespace(child2) Then Continue For

						If StrContainsAny(child2, True, "ven_1002") Then
							Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(subregkey, child2)
								If regkey3 IsNot Nothing Then
									For Each child3 As String In regkey3.GetSubKeyNames()
										If IsNullOrWhitespace(child3) Then Continue For
										'need to test more this code. got an error on a friend computer (Wagnard)(Possibly fixed with the trycast)
										Array = TryCast(MyRegistry.OpenSubKey(regkey3, child3).GetValue("LowerFilters"), String())
										If (Array IsNot Nothing) AndAlso Array.Length > 0 Then
											For Each entry As String In Array
												If IsNullOrWhitespace(entry) Then Continue For

												If StrContainsAny(entry, True, "amdkmafd") Then
													Application.Log.AddWarningMessage("Found a remaining AMD audio controller bus ! Preventing the removal of its driverfiles.")
													donotremoveamdhdaudiobusfiles = True
												End If
											Next
										End If
									Next
								End If
							End Using
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
			donotremoveamdhdaudiobusfiles = True  ' A security if the code to check fail.
		End Try

		If config.SelectedGPU = GPUVendor.Nvidia Then
			'nVidia AudioEndpoints Removal
			Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("audioendpoint", Nothing, False)
			If found.Count > 0 Then
				For Each d As SetupAPI.Device In found
					If d IsNot Nothing Then
						If StrContainsAny(d.FriendlyName, True, "nvidia virtual audio device", "nvidia high definition audio") Then
							SetupAPI.UninstallDevice(d)
						End If
					End If
				Next
				found.Clear()
			End If
		End If

		If config.SelectedGPU = GPUVendor.AMD Then
			' ------------------------------
			' Removing some of AMD AudioEndpoints
			' ------------------------------
			Application.Log.AddMessage("Removing AMD Audio Endpoints")
			Try
				'AMD AudioEndpoints Removal
				Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("audioendpoint")
				If found.Count > 0 Then
					For Each d As SetupAPI.Device In found
						If StrContainsAny(d.FriendlyName, True, "amd high definition audio device", "digital audio (hdmi) (high definition audio device)") Then
							SetupAPI.UninstallDevice(d)
						End If
					Next
					found.Clear()
				End If
			Catch ex As Exception
				MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButton.OK, MessageBoxImage.Error)
				Application.Log.AddException(ex)
			End Try
		End If

		'-----------------------
		'Removing NVVHCI
		'-----------------------
		If config.SelectedGPU = GPUVendor.Nvidia AndAlso config.RemoveGFE Then
			Try
				Application.Log.AddMessage("Executing SetupAPI: Remove NVVHCI.")
				Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("system", Nothing, False)
				If found.Count > 0 Then

					For Each d As SetupAPI.Device In found
						If d IsNot Nothing Then
							If StrContainsAny(d.HardwareIDs(0), True, "ROOT\NVVHCI") Then
								SetupAPI.UninstallDevice(d)
							End If
						End If
					Next

					found.Clear()
				End If
				Application.Log.AddMessage("SetupAPI: Remove NVVHCI Complete.")
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		' ----------------------
		' Removing the videocard
		' ----------------------

		Try
			Application.Log.AddMessage("Executing SetupAPI: Remove GPU(s).")
			Dim GPUs As List(Of SetupAPI.Device) = SetupAPI.GetDevicesByCHID(VendCHIDGPU, False, False, False)
			If GPUs.Count > 0 Then
				For Each GPU As SetupAPI.Device In GPUs
					If GPU IsNot Nothing Then
						If win10 Then
							Application.Log.AddMessage("Executing SetupAPI: Remove SoftwareComponent.")
							Dim SoftwareComponents As List(Of SetupAPI.Device) = SetupAPI.GetDevices("SoftwareComponent", Nothing, False, True)
							If SoftwareComponents.Count > 0 Then
								For Each SoftwareComponent As SetupAPI.Device In SoftwareComponents
									If SoftwareComponent.ParentDevices IsNot Nothing AndAlso SoftwareComponent.ParentDevices.Length > 0 Then
										For Each ParentDevice As SetupAPI.Device In SoftwareComponent.ParentDevices
											If ParentDevice IsNot Nothing Then
												If StrContainsAny(ParentDevice.DeviceID, True, GPU.DeviceID) Then
													SetupAPI.UninstallDevice(SoftwareComponent)
												End If
											End If
										Next
									End If
								Next

								SoftwareComponents.Clear()
							End If

							'Removing Software components (DCH stuff, win10+) (no parents, because old device is removed. SafeMode behavior)
							Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("SoftwareComponent", Nothing, False)
							If found.Count > 0 Then
								For Each d As SetupAPI.Device In found
									If d IsNot Nothing Then
										If StrContainsAny(d.HardwareIDs(0), True, VendidSC) Then
											SetupAPI.UninstallDevice(d)
										End If
									End If
								Next
								found.Clear()
							End If

							Application.Log.AddMessage("SetupAPI: Remove SoftwareComponent Complete.")
						End If
						SetupAPI.UninstallDevice(GPU) 'Then we remove the GPU itself.
					End If
				Next
				GPUs.Clear()
			End If
			UpdateTextMethod(UpdateTextTranslated(23))
			Application.Log.AddMessage("SetupAPI: Remove GPU(s) Complete.")
		Catch ex As Exception
			'MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error)
			Application.Log.AddException(ex)
			config.GPURemovedSuccess = False
			Exit Sub
		End Try



		CleanupEngine.Cleandriverstore(config)

		If config.SelectedGPU <> GPUVendor.Intel Then
			CleanupEngine.Cleandriverstore(config)
		End If

		'Here I remove 3dVision USB Adapter and USB type C(RTX).
		If config.SelectedGPU = GPUVendor.Nvidia Then

			Try
				Dim HWID3dvision As String() =
				 {"USB\VID_0955&PID_0007",
				  "USB\VID_0955&PID_7001",
				  "USB\VID_0955&PID_7002",
				  "USB\VID_0955&PID_7003",
				  "USB\VID_0955&PID_7004",
				  "USB\VID_0955&PID_7008",
				  "USB\VID_0955&PID_7009",
				  "USB\VID_0955&PID_700A",
				  "USB\VID_0955&PID_700C",
				  "USB\VID_0955&PID_700D&MI_00",
				  "USB\VID_0955&PID_700E&MI_00"}
				Dim USBTypeC As String() =
					{"PCI\VEN_10DE&DEV_1AD7",
					"PCI \ VEN_10DE&DEV_1AD9",
					"PCI\VEN_10DE&DEV_1ADB"}

				'3dVision Removal
				Application.Log.AddMessage("Executing SetupAPI: Remove 3dVision USB Adapter.")
				Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("media", Nothing, False)
				If found.Count > 0 Then
					For Each d As SetupAPI.Device In found
						If d IsNot Nothing Then
							If StrContainsAny(d.HardwareIDs(0), True, HWID3dvision) Then
								SetupAPI.UninstallDevice(d)
							End If
						End If
					Next
					found.Clear()
				End If
				Application.Log.AddMessage("SetupAPI: Remove 3dVision USB Adapter Complete.")

				'USB Type C Removal
				Application.Log.AddMessage("Executing SetupAPI: Remove USB type C(RTX).")
				found = SetupAPI.GetDevices("usb", Nothing, False)
				If found.Count > 0 Then
					For Each d As SetupAPI.Device In found
						If d IsNot Nothing Then
							If StrContainsAny(d.HardwareIDs(0), True, USBTypeC) Then
								SetupAPI.UninstallDevice(d)
							End If
						End If
					Next
					found.Clear()
				End If
				Application.Log.AddMessage("SetupAPI: Remove USB type C(RTX) Complete.")

				'NVIDIA SHIELD Wireless Controller Trackpad
				Application.Log.AddMessage("Executing SetupAPI: Remove NVIDIA SHIELD Wireless Controller Trackpad.")
				found = SetupAPI.GetDevices("mouse", Nothing, False)
				If found.Count > 0 Then
					For Each d As SetupAPI.Device In found
						If d IsNot Nothing Then
							If StrContainsAny(d.HardwareIDs(0), True, "hid\vid_0955&pid_7210") Then
								SetupAPI.UninstallDevice(d)
							End If
						End If
					Next
					found.Clear()
				End If
				Application.Log.AddMessage("SetupAPI: Remove NVIDIA SHIELD Wireless Controller Trackpad Complete.")

				If config.RemoveGFE Then
					' NVIDIA Virtual Audio Device (Wave Extensible) (WDM) Removal
					Application.Log.AddMessage("Executing SetupAPI: Remove NVIDIA Virtual Audio Device (Wave Extensible) (WDM).")
					found = SetupAPI.GetDevices("media", Nothing, False)
					If found.Count > 0 Then
						For Each d As SetupAPI.Device In found
							If StrContainsAny(d.HardwareIDs(0), True, "USB\VID_0955&PID_9000") Then
								SetupAPI.UninstallDevice(d)
							End If
						Next
						found.Clear()
					End If
					Application.Log.AddMessage("SetupAPI: Remove NVIDIA Virtual Audio Device (Wave Extensible) (WDM) Complete .")
				End If

			Catch ex As Exception
				Application.Log.AddException(ex)
				'MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error)
			End Try
		End If

		If config.SelectedGPU = GPUVendor.Intel Then

			'Removing Intel WIdI bus Enumerator
			Application.Log.AddMessage("Executing SetupAPI: Remove Intel WIdI bus Enumerator.")
			Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("system", Nothing, False)
			If found.Count > 0 Then
				For Each d As SetupAPI.Device In found
					If d.HasHardwareID AndAlso StrContainsAny(d.HardwareIDs(0), True, "root\iwdbus") Then  'Workaround for a bug report we got.
						SetupAPI.UninstallDevice(d)
					End If
				Next
				found.Clear()
			End If
			Application.Log.AddMessage("SetupAPI: Remove Intel WIdI bus Enumerator Complete .")
		End If

		Application.Log.AddMessage("SetupAPI: Remove Audio/HDMI Complete")

		'removing monitor and hidden monitor

		If config.RemoveMonitors Then
			Application.Log.AddMessage("Executing SetupAPI: Remove Monitor(s) started")
			Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("monitor", Nothing, False)
			If found.Count > 0 Then
				For Each d As SetupAPI.Device In found
					SetupAPI.UninstallDevice(d)
				Next
				found.Clear()
			End If
			UpdateTextMethod(UpdateTextTranslated(27))
			Application.Log.AddMessage("SetupAPI: Remove Monitor(s) Complete .")
		End If


		'here we set back to default the changes made by the AMDKMPFD even if we are cleaning amd or intel. We dont want that
		'espcially if we are not using an AMD GPU

		If config.RemoveAMDKMPFD Then
			Try
				UpdateTextMethod("Start - Check for AMDKMPFD system device.")
				Application.Log.AddMessage("Executing SetupAPI: check AMDKMPFD system device started")
				Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("system", "0a0", False)
				If found.Count > 0 Then
					For Each d As SetupAPI.Device In found
						If StrContainsAny(d.HardwareIDs(0), True, "DEV_0A08", "DEV_0A03") Then
							If d.LowerFilters IsNot Nothing AndAlso StrContainsAny(d.LowerFilters(0), True, "amdkmpfd") Then
								Application.Log.AddMessage("Executing SetupAPI: update AMDKMPFD system device to Windows default started")
								If win10 Then
									SetupAPI.UpdateDeviceInf(d, config.Paths.WinDir + "inf\PCI.inf", True)
								Else
									SetupAPI.UpdateDeviceInf(d, config.Paths.WinDir + "inf\machine.inf", True)
								End If
							End If
						End If
					Next
					found.Clear()
				End If
				UpdateTextMethod("End - Check for AMDKMPFD system device.")
				Application.Log.AddMessage("SetupAPI: Check AMDKMPFD system device Complete .")
			Catch ex As Exception
				Application.Log.AddException(ex)
				'MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error)
			End Try

			UpdateTextMethod(UpdateTextTranslated(28))


			'We now try to remove the service AMDPMPFD if its lowerfilter is not found
			If config.Restart Or config.Shutdown Then
				If Not Checkamdkmpfd() Then
					UpdateTextMethod("Start - Check for AMDKMPFD service.")
					CleanupEngine.Cleanserviceprocess({"amdkmpfd"})
					UpdateTextMethod("End - Check for AMDKMPFD service.")
				End If
			End If
		End If
		ImpersonateLoggedOnUser.Taketoken()

		If config.SelectedGPU = GPUVendor.AMD Then
			Cleanamdserviceprocess()
			Cleanamd(config)

			Cleanamdfolders(config)
		End If

		If config.SelectedGPU = GPUVendor.Nvidia Then
			Checkpcieroot(config)
			Cleannvidiaserviceprocess(config)
			Cleannvidia(config)
			Cleannvidiafolders(config)
			CleanupEngine.RemoveRegDeviceSoftware("NVIDIA CoInstaller Display.Driver")

		End If

		If config.SelectedGPU = GPUVendor.Intel Then
			cleanintelserviceprocess()
			cleanintel(config)
			cleanintelfolders()
		End If

		CleanupEngine.Cleandriverstore(config)
		CleanupEngine.Fixregistrydriverstore(config)

		config.Success = True

	End Sub

	Private Sub KillProcess(ByVal ParamArray processnames As String())
		For Each processName As String In processnames
			If String.IsNullOrEmpty(processName) Then
				Continue For
			End If

			For Each process As Process In Process.GetProcessesByName(processName)
				Try
					process.Kill()
				Catch ex As Exception
					Application.Log.AddExceptionWithValues(ex, "@KillProcess()", String.Concat("ProcessName: ", processName))
				End Try
			Next
		Next
	End Sub

	Private Sub KillGPUStatsProcesses()
		' Not sure for the x86 one...
		' Shady: probably the same but without _x64, and a few sites seem to confirm this, doesn't hurt to just add it anyway

		KillProcess(
		 "MSIAfterburner",
		  "PrecisionX_x64",
		  "PrecisionXServer_x64",
		  "PrecisionX",
		  "PrecisionXServer",
		  "RTSS",
		  "RTSSHooksLoader64",
		  "EncoderServer64",
		  "RTSSHooksLoader",
		  "EncoderServer",
		  "nvidiaInspector")
	End Sub

	Private Sub Cleanamdserviceprocess()

		Application.Log.AddMessage("Cleaning Process/Services...")

		CleanupEngine.Cleanserviceprocess(IO.File.ReadAllLines(Application.Paths.AppBase & "settings\AMD\services.cfg"))    '// add each line as String Array.

		Dim killpid As New ProcessStartInfo
		killpid.FileName = "cmd.exe"
		killpid.Arguments = " /C" & "taskkill /f /im CLIStart.exe"
		killpid.UseShellExecute = False
		killpid.CreateNoWindow = True
		killpid.RedirectStandardOutput = False

		Dim processkillpid As New Process
		processkillpid.StartInfo = killpid
		processkillpid.Start()
		processkillpid.WaitForExit()
		processkillpid.Close()

		KillProcess(
		 "MOM",
		 "CLIStart",
		 "CLI",
		 "CCC",
		 "Cnext",
		 "HydraDM",
		 "HydraDM64",
		 "HydraGrd",
		 "Grid64",
		 "HydraMD64",
		 "HydraMD",
		 "RadeonSettings",
		 "ThumbnailExtractionHost",
		 "jusched")
		Application.Log.AddMessage("Process/Services CleanUP Complete")
		timer.Interval = 10
		timer.AutoReset = False
		While True
			timer.Start()
			objAuto.WaitOne()
			Exit While
		End While
	End Sub

	Private Sub Cleanamd(ByVal config As ThreadSettings)

		Dim wantedvalue As String = Nothing
		Dim wantedvalue2 As String = Nothing
		Dim filePath As String = Nothing
		Dim packages As String()
		Dim Thread2Finished As Boolean = False


		UpdateTextMethod(UpdateTextTranslated(2))
		Application.Log.AddMessage("Cleaning known Regkeys")


		'Delete AMD regkey
		'Deleting DCOM object

		Application.Log.AddMessage("Starting dcom/clsid/appid/typelib cleanup")

		CleanupEngine.ClassRoot(IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\classroot.cfg"), config)  '// add each line as String Array.


		'-----------------
		'interface cleanup
		'-----------------



		CleanupEngine.Interfaces(IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\interface.cfg"))    '// add each line as String Array.

		Application.Log.AddMessage("Instance class cleanUP")
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "CLSID", False)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "CLSID\" & child, False)
								If subregkey IsNot Nothing Then
									Using subregkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "CLSID\" & child & "\Instance", False)
										If subregkey2 IsNot Nothing Then
											For Each child2 As String In subregkey2.GetSubKeyNames()
												If IsNullOrWhitespace(child2) = False Then
													Using superkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "CLSID\" & child & "\Instance\" & child2)
														If superkey IsNot Nothing Then
															If Not IsNullOrWhitespace(superkey.GetValue("FriendlyName", String.Empty).ToString) Then
																wantedvalue2 = superkey.GetValue("FriendlyName", String.Empty).ToString
																If Not IsNullOrWhitespace(wantedvalue2) Then
																	If wantedvalue2.ToLower.Contains("ati mpeg") Or
																 wantedvalue2.ToLower.Contains("amd mjpeg") Or
																 wantedvalue2.ToLower.Contains("ati ticker") Or
																 wantedvalue2.ToLower.Contains("mmace softemu") Or
																 wantedvalue2.ToLower.Contains("mmace deinterlace") Or
																 wantedvalue2.ToLower.Contains("amd video") Or
																 wantedvalue2.ToLower.Contains("mmace procamp") Or
																 wantedvalue2.ToLower.Contains("ati video") Then
																		Try
																			Deletesubregkey(Registry.ClassesRoot, "CLSID\" & child & "\Instance\" & child2)
																		Catch ex As Exception
																		End Try
																	End If
																End If
															End If
														End If
													End Using
												End If
											Next
										End If
									End Using
								End If
							End Using
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\CLSID", False)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then
								Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\CLSID\" & child, False)
									If subregkey IsNot Nothing Then
										Using subregkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\CLSID\" & child & "\Instance", False)
											If subregkey2 IsNot Nothing Then
												For Each child2 As String In subregkey2.GetSubKeyNames()
													If IsNullOrWhitespace(child2) = False Then
														Using superkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\CLSID\" & child & "\Instance\" & child2)
															If superkey IsNot Nothing Then
																If IsNullOrWhitespace(superkey.GetValue("FriendlyName", String.Empty).ToString) = False Then
																	wantedvalue2 = superkey.GetValue("FriendlyName", String.Empty).ToString
																	If wantedvalue2.ToLower.Contains("ati mpeg") Or
																	wantedvalue2.ToLower.Contains("amd mjpeg") Or
																	wantedvalue2.ToLower.Contains("ati ticker") Or
																	wantedvalue2.ToLower.Contains("mmace softemu") Or
																	wantedvalue2.ToLower.Contains("mmace deinterlace") Or
																	wantedvalue2.ToLower.Contains("mmace procamp") Or
																	wantedvalue2.ToLower.Contains("amd video") Or
																	wantedvalue2.ToLower.Contains("ati video") Then
																		Try
																			Deletesubregkey(Registry.ClassesRoot, "Wow6432Node\CLSID\" & child & "\Instance\" & child2)
																		Catch ex As Exception
																		End Try
																	End If
																End If
															End If
														End Using
													End If
												Next
											End If
										End Using
									End If
								End Using
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		Application.Log.AddMessage("MediaFoundation cleanUP")
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "MediaFoundation\Transforms", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
							If regkey2 IsNot Nothing Then
								If IsNullOrWhitespace(regkey2.GetValue("", String.Empty).ToString) Then Continue For

								If StrContainsAny(regkey2.GetValue("").ToString, True, "amd d3d11 hardware mft", "amd fast (dnd) decoder", "amd h.264 hardware mft encoder", "amd playback decoder mft") Then
									Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey, "Categories")
										If regkey3 IsNot Nothing Then
											For Each child2 As String In regkey3.GetSubKeyNames
												If IsNullOrWhitespace(child2) Then Continue For
												Using regkey4 As RegistryKey = MyRegistry.OpenSubKey(regkey, "Categories\" & child2, True)
													If regkey4 IsNot Nothing Then
														Try
															Deletesubregkey(regkey4, child)
														Catch ex As Exception
														End Try
													End If
												End Using
											Next
										End If
									End Using
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								End If
							End If
						End Using
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\MediaFoundation\Transforms", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For

							Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
								If regkey2 IsNot Nothing Then
									If IsNullOrWhitespace(regkey2.GetValue("", String.Empty).ToString) Then Continue For

									If StrContainsAny(regkey2.GetValue("").ToString, True, "amd d3d11 hardware mft", "amd fast (dnd) decoder", "amd h.264 hardware mft encoder", "amd playback decoder mft") Then
										Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey, "Categories")
											If regkey3 IsNot Nothing Then
												For Each child2 As String In regkey3.GetSubKeyNames
													If IsNullOrWhitespace(child2) Then Continue For
													Using regkey4 As RegistryKey = MyRegistry.OpenSubKey(regkey, "Categories\" & child2, True)
														If regkey4 IsNot Nothing Then
															Try
																Deletesubregkey(regkey4, child)
															Catch ex As Exception
															End Try
														End If
													End Using
												Next
											End If
										End Using
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
									End If
								End If
							End Using
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		Application.Log.AddMessage("AppID and clsidleftover cleanUP")
		'old dcom 

		Dim thread2 As Thread = New Thread(Sub() CLSIDCleanThread(Thread2Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\clsidleftover.cfg")))
		thread2.Start()

		Application.Log.AddMessage("Record CleanUP")

		'--------------
		'Record cleanup
		'--------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Record", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
							If subregkey IsNot Nothing Then
								For Each childs As String In subregkey.GetSubKeyNames()
									If IsNullOrWhitespace(childs) Then Continue For

									Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(subregkey, childs, False)
										If regkey2 IsNot Nothing Then
											If IsNullOrWhitespace(regkey2.GetValue("Assembly", String.Empty).ToString) Then Continue For

											If StrContainsAny(regkey2.GetValue("Assembly", String.Empty).ToString, True, "aticccom") Then
												Try
													Deletesubregkey(regkey, child)
												Catch ex As Exception
												End Try
											End If
										End If
									End Using
								Next
							End If
						End Using
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try
		Application.Log.AddMessage("Assembly CleanUP")

		'------------------
		'Assemblies cleanUP
		'------------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Classes\Installer\Assemblies", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("ati.ace") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try

							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'----------------------
		'End Assemblies cleanUP
		'----------------------


		'end of decom?
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Controls Folder\" &
		  "Display\shellex\PropertySheetHandlers", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "ATIACE")
				Catch ex As Exception
				End Try
			End If
		End Using

		If config.RemoveVulkan Then
			CleanVulkan(config)
		End If
		Application.Log.AddMessage("ngenservice Clean")

		'----------------------
		'.net ngenservice clean
		'----------------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\.NETFramework\v2.0.50727\NGenService\Roots", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("ati.ace") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'-----------------------------
		'End of .net ngenservice clean
		'-----------------------------

		'-----------------------------
		'Shell extensions\aprouved
		'-----------------------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) = False Then
							If regkey.GetValue(child).ToString.ToLower.Contains("catalyst context menu extension") Or
							 regkey.GetValue(child).ToString.ToLower.Contains("display cpl extension") Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames()
							If IsNullOrWhitespace(child) = False Then
								If regkey.GetValue(child).ToString.ToLower.Contains("catalyst context menu extension") Or
								 regkey.GetValue(child).ToString.ToLower.Contains("display cpl extension") Then
									Try
										Deletevalue(regkey, child)
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								End If
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If
		'-----------------------------
		'End Shell extensions\aprouved
		'-----------------------------

		Application.Log.AddMessage("Pnplockdownfiles region cleanUP")

		CleanupEngine.Pnplockdownfiles(IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\driverfiles.cfg"))   '// add each line as String Array.

		Try

			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Khronos")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\AMD")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\ATI Technologies")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\Services\Atierecord")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\Services\amdkmdap")
		Catch ex As Exception
		End Try

		If IntPtr.Size = 8 Then
			Try
				Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\Khronos")
			Catch ex As Exception
			End Try

			Try
				Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\ATI\ACE")
			Catch ex As Exception
			End Try
		End If

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\AMD\EEU")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\Services\Atierecord\eRecordEnable")
		Catch ex As Exception
		End Try

		Try
			Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\Services\Atierecord\eRecordEnablePopups")
		Catch ex As Exception
		End Try

		'---------------------------------------------
		'Cleaning of Legacy_AMDKMDAG+ on win7 and lower
		'---------------------------------------------

		Try
			If config.WinVersion < OSVersion.Win81 AndAlso WinForm.SystemInformation.BootMode <> WinForm.BootMode.Normal Then 'win 7 and lower + safemode only
				Application.Log.AddMessage("Cleaning LEGACY_AMDKMDAG")
				Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				 "SYSTEM")
					If subregkey IsNot Nothing Then
						For Each childs As String In subregkey.GetSubKeyNames()
							If IsNullOrWhitespace(childs) = False Then
								If StrContainsAny(childs, True, "controlset") Then
									Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
									  "SYSTEM\" & childs & "\Enum\Root")
										If regkey IsNot Nothing Then
											For Each child As String In regkey.GetSubKeyNames()
												If IsNullOrWhitespace(child) Then Continue For
												If child.ToLower.Contains("legacy_amdkmdag") Or
												 (child.ToLower.Contains("legacy_amdkmpfd") AndAlso config.RemoveAMDKMPFD) Or
												 child.ToLower.Contains("legacy_amdacpksd") Then

													Try
														Deletesubregkey(Registry.LocalMachine, "SYSTEM\" & childs & "\Enum\Root\" & child)
													Catch ex As Exception
														Application.Log.AddException(ex)
													End Try
												End If
											Next
										End If
									End Using
								End If
							End If
						Next
					End If
				End Using
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'----------------------------------------------------
		'End of Cleaning of Legacy_AMDKMDAG on win7 and lower
		'----------------------------------------------------


		'--------------------------------
		'System environement path cleanup
		'--------------------------------
		Application.Log.AddMessage("System environement cleanUP")
		Try
			Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
				If subregkey IsNot Nothing Then
					For Each child2 As String In subregkey.GetSubKeyNames()
						If IsNullOrWhitespace(child2) Then Continue For
						If StrContainsAny(child2, True, "controlset") Then
							Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Control\Session Manager\Environment", True)
								If regkey IsNot Nothing Then
									For Each child As String In regkey.GetValueNames()
										If IsNullOrWhitespace(child) Then Continue For
										If child.Contains("AMDAPPSDKROOT") Then
											Try
												Deletesubregkey(regkey, child)
											Catch ex As Exception
												Application.Log.AddException(ex)
											End Try
										End If
										If child.Contains("Path") Then
											If Not IsNullOrWhitespace(regkey.GetValue(child, String.Empty).ToString) Then
												wantedvalue = regkey.GetValue(child, String.Empty).ToString.ToLower
												If Not IsNullOrWhitespace(wantedvalue) Then
													Try
														Select Case True
															Case wantedvalue.Contains(";" + sysdrv & "program files (x86)\amd app\bin\x86_64")
																wantedvalue = wantedvalue.Replace(";" + sysdrv & "program files (x86)\amd app\bin\x86_64", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(sysdrv & "program files (x86)\amd app\bin\x86_64;")
																wantedvalue = wantedvalue.Replace(sysdrv & "program files (x86)\amd app\bin\x86_64;", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(";" + sysdrv & "program files (x86)\amd app\bin\x86")
																wantedvalue = wantedvalue.Replace(";" + sysdrv & "program files (x86)\amd app\bin\x86", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(sysdrv & "program files (x86)\amd app\bin\x86;")
																wantedvalue = wantedvalue.Replace(sysdrv & "program files (x86)\amd app\bin\x86;", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(";" + sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static")
																wantedvalue = wantedvalue.Replace(";" + sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static;")
																wantedvalue = wantedvalue.Replace(sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static;", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(";" + sysdrv & "program Files (x86)\amd\ati.ace\core-static")
																wantedvalue = wantedvalue.Replace(";" + sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static", "")
																regkey.SetValue(child, wantedvalue)

															Case wantedvalue.Contains(sysdrv & "program Files (x86)\amd\ati.ace\core-static;")
																wantedvalue = wantedvalue.Replace(sysdrv & "program Files (x86)\ati technologies\ati.ace\core-static;", "")
																regkey.SetValue(child, wantedvalue)

														End Select
													Catch ex As Exception
													End Try
												End If
											End If
										End If
									Next
								End If
							End Using
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'end system environement patch cleanup

		'-----------------------
		'remove event view stuff
		'-----------------------
		Application.Log.AddMessage("Remove eventviewer stuff")
		Try
			Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
				If subregkey IsNot Nothing Then
					For Each child2 As String In subregkey.GetSubKeyNames()
						If IsNullOrWhitespace(child2) Then Continue For
						If StrContainsAny(child2, True, "controlset") Then
							Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Services\eventlog", True)
								If regkey IsNot Nothing Then
									For Each child As String In regkey.GetSubKeyNames()
										If IsNullOrWhitespace(child) Then Continue For
										If child.ToLower.Contains("aceeventlog") Then
											Deletesubregkey(regkey, child)
										End If
									Next


									Try
										Deletesubregkey(regkey, "Application\ATIeRecord")
									Catch ex As Exception
									End Try

									Try
										Deletesubregkey(regkey, "System\amdkmdag")
									Catch ex As Exception
									End Try

									Try
										Deletesubregkey(regkey, "System\amdkmdap")
									Catch ex As Exception
									End Try
								End If
							End Using
							Try
								Deletesubregkey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Services\Atierecord")
							Catch ex As Exception
							End Try
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		'--------------------------------
		'end of eventviewer stuff removal
		'--------------------------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot,
			 "Directory\background\shellex\ContextMenuHandlers", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For
						If child.Contains("ACE") Then

							Deletesubregkey(regkey, child)

						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		' to fix later, the range is too large and could lead to problems.
		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If IsNullOrWhitespace(users) Then Continue For
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							If child.StartsWith("ATI") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						Next
					End If
				End Using

				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames()
							If IsNullOrWhitespace(child) Then Continue For

							If StrContainsAny(child, True, "radeonsettings.exe", "amdrsserv.exe") Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						Next
					End If
				End Using
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		' to fix later, the range is too large and could lead to problems.
		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If IsNullOrWhitespace(users) Then Continue For
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							If child.StartsWith("AMD") Then
								Deletesubregkey(regkey, child)
							End If
						Next
					End If
				End Using
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\ATI", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "ace", "appprofiles", "A4", "install") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "Software\ATI")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\ATI Technologies", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "cbt") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "ati catalyst control center") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "cds") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "log") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "prw") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "install") Then
								'here we check the install path location in case CCC is not installed on the system drive.  A kill to explorer must be made
								'to help cleaning in normal mode.
								If System.Windows.Forms.SystemInformation.BootMode = WinForm.BootMode.Normal Then
									Application.Log.AddMessage("Killing Explorer.exe")
									KillProcess("explorer")
								End If

								Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
									If regkey2 IsNot Nothing Then
										If Not IsNullOrWhitespace(regkey2.GetValue("InstallDir", String.Empty).ToString) Then

											filePath = regkey2.GetValue("InstallDir", String.Empty).ToString

											If Not IsNullOrWhitespace(filePath) AndAlso FileIO.ExistsDir(filePath) Then
												For Each childf As String In FileIO.GetDirectories(filePath)
													If IsNullOrWhitespace(childf) Then Continue For

													If StrContainsAny(childf, True, "ati.ace", "cnext", "amdkmpfd", "cim", "Performance Profile Client") Then
														Delete(childf)
													End If
												Next
												If FileIO.CountDirectories(filePath) = 0 Then

													Delete(filePath)

												End If
												If Not Directory.Exists(filePath) Then
													'here we will do a special environement path cleanup as there is chances that the installation is
													'somewhere else.
													AmdEnvironementPath(filePath)
												End If
											End If
										End If
										For Each child2 As String In regkey2.GetSubKeyNames()
											If IsNullOrWhitespace(child2) Then Continue For

											If StrContainsAny(child2, True, "A464", "ati catalyst", "ati mcat", "avt", "ccc", "cnext", "amd app sdk", "packages", "distribution", "ppc",
											   "wirelessdisplay", "hydravision", "avivo", "ati display driver", "installed drivers", "steadyvideo", "amd dvr", "ati problem report wizard", "amd problem report wizard", "cnbranding") Then
												Try
													Deletesubregkey(regkey2, child2)
												Catch ex As Exception
												End Try
											End If
										Next
										For Each values As String In regkey2.GetValueNames()
											If IsNullOrWhitespace(values) Then Continue For
											Try
												Deletevalue(regkey2, values) 'This is for windows 7, it prevent removing the South Bridge and fix the Catalyst "Upgrade"
											Catch ex As Exception
											End Try
										Next
										If regkey2.SubKeyCount = 0 Then
											Try
												Deletesubregkey(regkey, child)
											Catch ex As Exception
											End Try
										Else
											For Each data As String In regkey2.GetSubKeyNames()
												If IsNullOrWhitespace(data) Then Continue For
												Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
											Next
										End If
									End If
								End Using
							End If
						End If
					Next
					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "Software\ATI Technologies")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\AMD", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "eeu", "fuel", "cn", "chill", "mftvdecoder", "dvr", "gpu", "amdanalytics", "ppc") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
							If StrContainsAny(child, True, "install") Then  'Just a safety here....
								If regkey.OpenSubKey(child).SubKeyCount = 0 Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								End If
							End If
						End If
					Next
					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "Software\AMD")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\AMDDVR", True)
				If regkey IsNot Nothing Then

					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "Software\AMDDVR")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node\ATI", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then
								If StrContainsAny(child, True, "ace", "appprofiles", "A4") Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								End If
							End If
						Next
						If regkey.SubKeyCount = 0 Then
							Try
								Deletesubregkey(Registry.LocalMachine, "Software\Wow6432Node\ATI")
							Catch ex As Exception
							End Try
						Else
							For Each data As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
							Next
						End If
					End If
				End Using

				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node\AMD", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then
								If child.ToLower.Contains("eeu") Or
								   child.ToLower.Contains("mftvdecoder") Then

									Deletesubregkey(regkey, child)

								End If
							End If
						Next
						If regkey.SubKeyCount = 0 Then
							Deletesubregkey(Registry.LocalMachine, "Software\Wow6432Node\AMD")
						Else
							For Each data As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
							Next
						End If
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try

			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node\ATI Technologies", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then
								If StrContainsAny(child, True, "system wide settings", "log", "prw") Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								End If
								If child.ToLower.Contains("install") Then
									'here we check the install path location in case CCC is not installed on the system drive.  A kill to explorer must be made
									'to help cleaning in normal mode.
									If System.Windows.Forms.SystemInformation.BootMode = WinForm.BootMode.Normal Then
										Application.Log.AddMessage("Killing Explorer.exe")
										KillProcess("explorer")
									End If

									Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
										If regkey2 IsNot Nothing Then
											If Not IsNullOrWhitespace(regkey2.GetValue("InstallDir", String.Empty).ToString) Then

												filePath = regkey2.GetValue("InstallDir", String.Empty).ToString
												If Not IsNullOrWhitespace(filePath) AndAlso FileIO.ExistsDir(filePath) Then
													For Each childf As String In FileIO.GetDirectories(filePath)
														If IsNullOrWhitespace(childf) Then Continue For

														If StrContainsAny(childf, True, "ati.ace", "cnext", "amdkmpfd", "cim") Then

															Delete(childf)

														End If
													Next
													If FileIO.CountDirectories(filePath) = 0 Then

														Delete(filePath)

													End If
												End If
											End If

											For Each child2 As String In regkey2.GetSubKeyNames()
												If IsNullOrWhitespace(child2) Then Continue For

												If StrContainsAny(child2, True, "A464", "ati catalyst", "ati mcat", "avt", "ccc", "cnext", "packages",
												   "wirelessdisplay", "hydravision", "dndtranscoding64", "avivo", "steadyvideo", "amd app sdk runtime", "amd media foundation decoders") Then
													Try
														Deletesubregkey(regkey2, child2)
													Catch ex As Exception
													End Try
												End If
											Next
											If regkey2.SubKeyCount = 0 Then
												Try
													Deletesubregkey(regkey, child)
												Catch ex As Exception
												End Try
											Else
												For Each data As String In regkey2.GetSubKeyNames()
													If IsNullOrWhitespace(data) Then Continue For
													Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
												Next
											End If
										End If
									End Using
								End If
							End If
						Next
						If regkey.SubKeyCount = 0 Then
							Try
								Deletesubregkey(Registry.LocalMachine, "Software\Wow6432Node\ATI Technologies")
							Catch ex As Exception
							End Try
						Else
							For Each data As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
							Next
						End If
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If Not IsNullOrWhitespace(users) Then
					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software\Microsoft\Windows\CurrentVersion\Run", True)
						If regkey IsNot Nothing Then
							For Each child As String In regkey.GetValueNames
								If IsNullOrWhitespace(child) Then Continue For

								If StrContainsAny(child, True, "HydraVisionDesktopManager", "Grid", "HydraVisionMDEngine", "AMDDVR") Then
									Deletevalue(regkey, child)
								End If
							Next
						End If
					End Using
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Application.Log.AddMessage("Removing known Packages")

		packages = IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\packages.cfg")   '// add each line as String Array.
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			"Software\Microsoft\Windows\CurrentVersion\Uninstall", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Microsoft\Windows\CurrentVersion\Uninstall\" & child)

							If subregkey IsNot Nothing Then
								If IsNullOrWhitespace(subregkey.GetValue("DisplayName", String.Empty).ToString) Then Continue For
								wantedvalue = subregkey.GetValue("DisplayName", String.Empty).ToString
								If IsNullOrWhitespace(wantedvalue) Then Continue For
								For i As Integer = 0 To packages.Length - 1
									If IsNullOrWhitespace(packages(i)) Then Continue For
									If StrContainsAny(wantedvalue, True, packages(i)) Then
										Try
											If Not (config.RemoveVulkan = False AndAlso StrContainsAny(wantedvalue, True, "vulkan")) Then
												Deletesubregkey(regkey, child)
											End If
										Catch ex As Exception
											Application.Log.AddException(ex)
										End Try
									End If
								Next
							End If
						End Using
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			packages = IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\packages.cfg")   '// add each line as String Array.
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				 "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
								 "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" & child, True)
								If subregkey IsNot Nothing Then
									If IsNullOrWhitespace(subregkey.GetValue("DisplayName", String.Empty).ToString) Then Continue For
									wantedvalue = subregkey.GetValue("DisplayName", String.Empty).ToString
									If IsNullOrWhitespace(wantedvalue) Then Continue For
									For i As Integer = 0 To packages.Length - 1
										If Not IsNullOrWhitespace(packages(i)) Then
											If StrContainsAny(wantedvalue, True, packages(i)) Then
												Try
													Deletesubregkey(regkey, child)
												Catch ex As Exception
													Application.Log.AddException(ex)
												End Try
											End If
										End If
									Next
								End If
							End Using
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		CleanupEngine.Installer(IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\packages.cfg"), config)

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			 "Software\Microsoft\Windows\CurrentVersion\Run", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames
						If Not IsNullOrWhitespace(child) Then
							If StrContainsAny(child, True, "StartCCC", "StartCN", "AMD AVT") Then
								Deletevalue(regkey, child)
							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				 "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames
							If Not IsNullOrWhitespace(child) Then
								If StrContainsAny(child, True, "StartCCC", "StartCN", "AMD AVT") Then
									Deletevalue(regkey, child)
								End If
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			 "Software\Microsoft\Windows\CurrentVersion\Installer\Folders", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) Then Continue For

						If child.Contains("ATI\CIM\") Or
						   child.Contains("AMD\CNext\") Or
						   child.Contains("AMD APP\") Or
						   child.Contains("AMD\SteadyVideo\") Or
						   child.Contains("HydraVision\") Then

							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
							End Try
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'prevent CCC reinstalltion (comes from drivers installed from windows updates)
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) Then Continue For
						If child.ToLower.Contains("launchwuapp") Then
							Deletevalue(regkey, child)
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames()
							If IsNullOrWhitespace(child) Then Continue For
							If child.ToLower.Contains("launchwuapp") Then
								Deletevalue(regkey, child)
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		'Saw on Win 10 cat 15.7
		Application.Log.AddMessage("AudioEngine CleanUP")
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "AudioEngine\AudioProcessingObjects", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
							If regkey2 IsNot Nothing Then
								If IsNullOrWhitespace(regkey2.GetValue("FriendlyName", String.Empty).ToString) Then Continue For

								If StrContainsAny(regkey2.GetValue("FriendlyName", String.Empty).ToString, True, "cdelayapogfx") Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								End If
							End If
						End Using
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'SteadyVideo stuff

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
		 "Software\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For
					Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, False)
						If subregkey IsNot Nothing Then
							If Not IsNullOrWhitespace(subregkey.GetValue("", String.Empty).ToString) Then
								wantedvalue = subregkey.GetValue("", String.Empty).ToString
								If Not IsNullOrWhitespace(wantedvalue) Then
									If StrContainsAny(wantedvalue, True, "steadyvideo") Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
									End If
								End If
							End If
						End If
					End Using
				Next
			End If
		End Using

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "PROTOCOLS\Filter", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For
						Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, False)
							If subregkey IsNot Nothing Then
								If Not IsNullOrWhitespace(subregkey.GetValue("", String.Empty).ToString) Then
									wantedvalue = subregkey.GetValue("", String.Empty).ToString
									If Not IsNullOrWhitespace(wantedvalue) Then
										If wantedvalue.ToLower.Contains("steadyvideo") Then
											Try
												Deletesubregkey(regkey, child)
											Catch ex As Exception
												Application.Log.AddException(ex)
											End Try
										End If
									End If
								End If
							End If
						End Using
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			'SteadyVideo stuff

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For
						Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, False)
							If subregkey IsNot Nothing Then
								If Not IsNullOrWhitespace(subregkey.GetValue("", String.Empty).ToString) Then
									wantedvalue = subregkey.GetValue("", String.Empty).ToString
									If Not IsNullOrWhitespace(wantedvalue) Then
										If wantedvalue.ToLower.Contains("steadyvideo") Then
											Try
												Deletesubregkey(regkey, child)
											Catch ex As Exception
											End Try
										End If
									End If
								End If
							End If
						End Using
					Next
				End If
			End Using


			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Wow6432Node\PROTOCOLS\Filter", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, False)
								If subregkey IsNot Nothing Then
									If Not IsNullOrWhitespace(subregkey.GetValue("", String.Empty).ToString) Then
										wantedvalue = subregkey.GetValue("", String.Empty).ToString
										If Not IsNullOrWhitespace(wantedvalue) Then
											If StrContainsAny(wantedvalue, True, "steadyvideo") Then
												Try
													Deletesubregkey(regkey, child)
												Catch ex As Exception
													Application.Log.AddException(ex)
												End Try
											End If
										End If
									End If
								End If
							End Using
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If

		'Task Scheduler cleanUP (AMD Updater)
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames
					If IsNullOrWhitespace(child) Then Continue For
					Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
						If regkey2 IsNot Nothing Then
							If Not IsNullOrWhitespace(regkey2.GetValue("Description", String.Empty).ToString) Then
								If StrContainsAny(regkey2.GetValue("Description", String.Empty).ToString, True, "AMD Updater", "AMDLinkUpdate", "ModifyLinkUpdate") Then
									Deletesubregkey(regkey, child)
								End If
							End If
							If Not IsNullOrWhitespace(regkey2.GetValue("Path", String.Empty).ToString) Then
								If StrContainsAny(regkey2.GetValue("Path", String.Empty).ToString, True, "\StartCN", "\StartCNBM") Then
									Deletesubregkey(regkey, child)
								End If
							End If
						End If
					End Using
				Next
			End If
		End Using

		Using schedule As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache", True)
			If schedule IsNot Nothing Then
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(schedule, "Tree", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames
							If IsNullOrWhitespace(child) Then Continue For
							If StrContainsAny(child, True, "AMD Updater", "AMDLinkUpdate", "StartCN", "StartDVR", "StartCNBM", "ModifyLinkUpdate") Then
								For Each ScheduleChild As String In schedule.GetSubKeyNames
									If IsNullOrWhitespace(ScheduleChild) Then Continue For
									Try
										Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
											If regkey2 IsNot Nothing Then
												If Not IsNullOrWhitespace(regkey2.GetValue("Id", String.Empty).ToString) Then
													wantedvalue = regkey2.GetValue("Id", String.Empty).ToString
													Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(schedule, ScheduleChild, True)
														If regkey3 IsNot Nothing Then
															For Each child2 As String In regkey3.GetSubKeyNames
																If IsNullOrWhitespace(child2) Then Continue For
																If StrContainsAny(wantedvalue, True, child2) Then
																	Deletesubregkey(regkey3, child2)
																End If
															Next
														End If
													End Using
												End If
											End If
										End Using
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								Next
								Deletesubregkey(regkey, child)
							End If
						Next
					End If
				End Using
			End If
		End Using

		'      Dim OldValue As String = Nothing
		'Select Case System.Windows.Forms.SystemInformation.BootMode
		'          Case Forms.BootMode.FailSafe
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If
		'	Case Forms.BootMode.FailSafeWithNetwork
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If
		'	Case Forms.BootMode.Normal
		'		'Usually this service is Running in normal mode, we *could* in the future check all this.
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If
		'      End Select
		'Using tsc As New TaskSchedulerControl(config)
		'	For Each task As Task In tsc.GetAllTasks
		'		If StrContainsAny(task.Name, True, "AMD Updater", "StartCN") Then
		'			Try
		'				task.Delete()
		'			Catch ex As Exception
		'				Application.Log.AddException(ex)
		'			End Try
		'			Application.Log.AddMessage("TaskScheduler: " & task.Name & " as been removed")
		'		End If
		'	Next
		'End Using

		'Select Case System.Windows.Forms.SystemInformation.BootMode
		'	Case Forms.BootMode.FailSafe
		'              StopService("Schedule")
		'              If OldValue IsNot Nothing Then
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'	Case Forms.BootMode.FailSafeWithNetwork
		'              StopService("Schedule")
		'              If OldValue IsNot Nothing Then
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'	Case Forms.BootMode.Normal
		'              'Usually this service is running in normal mode, we don't need to stop it.
		'              If OldValue IsNot Nothing Then
		'                  StopService("Schedule")
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'End Select

		'Killing Explorer.exe to help releasing file that were open.
		timer.Interval = 500
		timer.AutoReset = False
		While Thread2Finished <> True
			timer.Start()
			objAuto.WaitOne()
		End While

		Application.Log.AddMessage("Killing Explorer.exe")
		KillProcess("explorer")

	End Sub
	Private Sub TimerElapsed(source As Object, e As System.Timers.ElapsedEventArgs)
		objAuto.Set()
	End Sub
	Private Sub Cleanamdfolders(ByVal config As ThreadSettings)
		Dim filePath As String = Nothing
		Dim removedxcache As Boolean = config.RemoveCrimsonCache
		Dim Thread1Finished = False

		'Delete AMD data Folders
		UpdateTextMethod(UpdateTextTranslated(1))

		Application.Log.AddMessage("Cleaning Directory (Please Wait...)")


		If config.RemoveAMDDirs Then
			filePath = sysdrv + "AMD"

			Delete(filePath)

		End If

		'Delete driver files
		'delete OpenCL

		Dim thread1 As Thread = New Thread(Sub() Threaddata1(Thread1Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\AMD\driverfiles.cfg")))
		thread1.Start()


		filePath = Environment.GetEnvironmentVariable("windir")
		Try
			Delete(filePath + "\atiogl.xml")
		Catch ex As Exception
		End Try

		filePath = Environment.GetEnvironmentVariable("windir")
		Try
			Delete(filePath + "\ativpsrm.bin")
		Catch ex As Exception
		End Try


		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.ProgramFiles) + "\ATI Technologies"
		If FileIO.ExistsDir(filePath) Then

			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("ati.ace") Or
				   child.ToLower.Contains("ati catalyst control center") Or
				   child.ToLower.Contains("application profiles") Or
				   child.ToLower.EndsWith("\px") Or
				   child.ToLower.Contains("hydravision") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next
			End If
		End If


		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.ProgramFiles) + "\ATI"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("cim") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If


		filePath = Environment.GetFolderPath _
		  (Environment.SpecialFolder.ProgramFiles) + "\Common Files" + "\ATI Technologies"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("multimedia") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.ProgramFiles) + "\AMD APP"
		If FileIO.ExistsDir(filePath) Then

			Delete(filePath)

		End If

		If IntPtr.Size = 8 Then

			filePath = Environment.GetFolderPath _
			  (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD AVT"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If

			filePath = Environment.GetFolderPath _
			 (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\ATI Technologies"
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("ati.ace") Or
							 child.ToLower.Contains("ati catalyst control center") Or
							 child.ToLower.Contains("application profiles") Or
							 child.ToLower.EndsWith("\px") Or
							 child.ToLower.Contains("hydravision") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			End If

			filePath = System.Environment.SystemDirectory
			If FileIO.ExistsDir(filePath) Then
				Dim files() As String = IO.Directory.GetFiles(filePath + "\", "coinst_*.*")
				For i As Integer = 0 To files.Length - 1
					If Not IsNullOrWhitespace(files(i)) Then
						Delete(files(i))
					End If
				Next
			End If

			filePath = Environment.GetFolderPath _
			   (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD APP"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If

			filePath = Environment.GetFolderPath _
			(Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD\SteadyVideo"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If

			filePath = Environment.GetFolderPath _
			(Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD\SteadyVideoFirefox"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If

			filePath = Environment.GetFolderPath _
			(Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD\SteadyVideoChrome"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If

			filePath = Environment.GetFolderPath _
			 (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\Common Files" + "\ATI Technologies"
			If FileIO.ExistsDir(filePath) Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("multimedia") Then

							Delete(child)

						End If
					End If
				Next
				Try
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			End If
		End If


		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Microsoft\Windows\Start Menu\Programs\Catalyst Control Center"
		If FileIO.ExistsDir(filePath) Then

			Delete(filePath)

		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Microsoft\Windows\Start Menu\Programs\AMD Problem Report Wizard"
		If FileIO.ExistsDir(filePath) Then

			Delete(filePath)

		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Microsoft\Windows\Start Menu\Programs\AMD Settings"
		If FileIO.ExistsDir(filePath) Then

			Delete(filePath)

		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Microsoft\Windows\Start Menu\Programs\AMD Catalyst Control Center"
		If FileIO.ExistsDir(filePath) Then

			Delete(filePath)

		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\ATI"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("ace") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\AMD"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If StrContainsAny(child, True, "kdb", "ppc", "fuel") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If

		For Each filepaths As String In FileIO.GetDirectories(config.Paths.UserPath)
			If IsNullOrWhitespace(filepaths) Then Continue For
			filePath = filepaths + "\AppData\Roaming\ATI"
			If winxp Then
				filePath = filepaths + "\Application Data\ATI"
			End If
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("ace") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
					Application.Log.AddMessage("Possible permission issue detected on : " + filePath)
				End Try
			End If


			filePath = filepaths + "\AppData\Local\ATI"
			If winxp Then
				filePath = filepaths + "\Local Settings\Application Data\ATI"
			End If
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("ace") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
					Application.Log.AddMessage("Possible permission issue detected on : " + filePath)
				End Try
			End If

			filePath = filepaths + "\AppData\Local\AMD"
			If winxp Then
				filePath = filepaths + "\Local Settings\Application Data\AMD"
			End If
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("cn") Or
							 child.ToLower.Contains("fuel") Or
							  child.ToLower.Contains("dvr") Or
							 removedxcache AndAlso child.ToLower.Contains("dxcache") Or
							 removedxcache AndAlso child.ToLower.Contains("glcache") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
					Application.Log.AddMessage("Possible permission issue detected on : " + filePath)
				End Try
			End If

			filePath = filepaths + "\AppData\Local\RadeonInstaller"
			If winxp Then
				filePath = filepaths + "\Local Settings\Application Data\RadeonInstaller"
			End If
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "cache", "QtWeb Engine") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
					Application.Log.AddMessage("Possible permission issue detected on : " + filePath)
				End Try
			End If

			filePath = filepaths + "\AppData\LocalLow\AMD"
			If winxp Then
				filePath = filepaths + "\Local Settings\Application Data\AMD"  'need check in the future.
			End If
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("cn") Or
							 child.ToLower.Contains("fuel") Or
							 removedxcache AndAlso child.ToLower.Contains("dxcache") Or
							 removedxcache AndAlso child.ToLower.Contains("glcache") Then

								Delete(child)

							End If
						End If
					Next
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
					Application.Log.AddMessage("Possible permission issue detected on : " + filePath)
				End Try
			End If

		Next

		'starting with AMD  14.12 Omega driver folders

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.ProgramFiles) + "\AMD"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
                    If StrContainsAny(child, True, "ccc2", "prw", "amdkmpfd", "cnext", "amdkmafd", "steadyvideo", "920dec42-4ca5-4d1d-9487-67be645cddfc", "cim", "performance profile client", "wvr") Then

                        Delete(child)

                    End If
                End If
			Next
			Try
				If FileIO.CountDirectories(filePath) = 0 Then

					Delete(filePath)

				Else
					For Each data As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
					Next

				End If
			Catch ex As Exception
			End Try
		End If

		filePath = Environment.GetFolderPath _
		  (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AMD"
		If FileIO.ExistsDir(filePath) Then

			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("ati.ace") Or
					   child.ToLower.Contains("cnext") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If

		'Cleaning the CCC assemblies.


		filePath = Environment.GetEnvironmentVariable("windir") + "\assembly\NativeImages_v4.0.30319_64"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.EndsWith("\mom") Or
					 child.ToLower.Contains("\mom.") Or
					 child.ToLower.Contains("newaem.foundation") Or
					 child.ToLower.Contains("fuel.foundation") Or
					 child.ToLower.Contains("\localizatio") Or
					 child.ToLower.EndsWith("\log") Or
					 child.ToLower.Contains("log.foundat") Or
					 child.ToLower.EndsWith("\cli") Or
					 child.ToLower.Contains("\cli.") Or
					 child.ToLower.Contains("ace.graphi") Or
					 child.ToLower.Contains("adl.foundation") Or
					 child.ToLower.Contains("64\aem.") Or
					 child.ToLower.Contains("aticccom") Or
					 child.ToLower.EndsWith("\ccc") Or
					 child.ToLower.Contains("\ccc.") Or
					 child.ToLower.Contains("\pckghlp.") Or
					 child.ToLower.Contains("\resourceman") Or
					 child.ToLower.Contains("\apm.") Or
					 child.ToLower.Contains("\a4.found") Or
					 child.ToLower.Contains("\atixclib") Or
					   child.ToLower.Contains("\dem.") Then

						Delete(child)

					End If
				End If
			Next
		End If

		filePath = Environment.GetEnvironmentVariable("windir") + "\assembly\GAC_MSIL"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.EndsWith("\mom") Or
					 child.ToLower.Contains("\mom.") Or
					 child.ToLower.Contains("newaem.foundation") Or
					 child.ToLower.Contains("fuel.foundation") Or
					 child.ToLower.Contains("\localizatio") Or
					 child.ToLower.EndsWith("\log") Or
					 child.ToLower.Contains("log.foundat") Or
					 child.ToLower.EndsWith("\cli") Or
					 child.ToLower.Contains("\cli.") Or
					 child.ToLower.Contains("ace.graphi") Or
					 child.ToLower.Contains("adl.foundation") Or
					 child.ToLower.Contains("64\aem.") Or
					 child.ToLower.Contains("msil\aem.") Or
					 child.ToLower.Contains("aticccom") Or
					 child.ToLower.EndsWith("\ccc") Or
					 child.ToLower.Contains("\ccc.") Or
					 child.ToLower.Contains("\pckghlp.") Or
					 child.ToLower.Contains("\resourceman") Or
					 child.ToLower.Contains("\apm.") Or
					 child.ToLower.Contains("\a4.found") Or
					 child.ToLower.Contains("\atixclib") Or
					 child.ToLower.Contains("\dem.") Then

						Delete(child)

					End If
				End If
			Next
		End If


		timer.Interval = 500
		timer.AutoReset = False
		While Thread1Finished <> True
			timer.Start()
			objAuto.WaitOne()
		End While

	End Sub

	Private Sub CleanEnvironementPath(ByVal valuesToRemove() As String)
		Dim value As String = Nothing

		Dim paths() As String = Nothing
		Dim newPaths As List(Of String)
		Dim removedPaths As List(Of String)

		'--------------------------------
		'System environment path cleanup
		'--------------------------------

		Dim logEntry As LogEntry = Application.Log.CreateEntry()
		logEntry.Message = "System Environment Path cleanUP"

		Try
			Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
				If subregkey IsNot Nothing Then
					For Each child2 As String In subregkey.GetSubKeyNames()
						If IsNullOrWhitespace(child2) Then Continue For
						If StrContainsAny(child2, True, "controlset") Then

							Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Control\Session Manager\Environment", True)
								If regkey IsNot Nothing Then
									For Each child As String In regkey.GetValueNames()
										If IsNullOrWhitespace(child) Then Continue For

										If child.Equals("Path", StringComparison.OrdinalIgnoreCase) Then
											value = regkey.GetValue(child, String.Empty).ToString()

											If Not IsNullOrWhitespace(value) Then
												paths = If(value.Contains(";"), value.Split(New Char() {";"c}, StringSplitOptions.None), New String() {value})

												newPaths = New List(Of String)(paths.Length)
												removedPaths = New List(Of String)(paths.Length)

												For Each p As String In paths
													If IsNullOrWhitespace(p) Then Continue For
													If Not StrContainsAny(p, True, valuesToRemove) Then 'StrContainsAny(..) checks p and each valuesToRemove for empty/null
														newPaths.Add(p)
													Else
														removedPaths.Add(p)
													End If
												Next

												logEntry.Add(child2, String.Join(Environment.NewLine, paths))
												logEntry.Add(KvP.Empty)

												If removedPaths.Count > 0 Then  'Change regkey's value only if modified
													regkey.SetValue(child, String.Join(";", newPaths.ToArray()))

													logEntry.Add(">> Removed", String.Join(Environment.NewLine, removedPaths.ToArray()))    'Log removed values
												Else
													logEntry.Add(">> Not modified")
												End If

												logEntry.Add(KvP.Empty)
												logEntry.Add(KvP.Empty)

												'	Select Case True
												'		Case value.Contains(";" + filepath & "\amd app\bin\x86_64")
												'			value = value.Replace(";" + filepath & "\amd app\bin\x86_64", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(filepath & "\amd app\bin\x86_64;")
												'			value = value.Replace(filepath & "\amd app\bin\x86_64;", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(";" + filepath & "\amd app\bin\x86")
												'			value = value.Replace(";" + filepath & "\amd app\bin\x86", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(filepath & "\amd app\bin\x86;")
												'			value = value.Replace(filepath & "\amd app\bin\x86;", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(";" + filepath & "\ati.ace\core-static")
												'			value = value.Replace(";" + filepath & "\ati.ace\core-static", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(filepath & "\ati.ace\core-static;")
												'			value = value.Replace(filepath & "\ati.ace\core-static;", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(";" + filepath & "\ati.ace\core-static")
												'			value = value.Replace(";" + filepath & "\ati.ace\core-static", "")
												'			regkey.SetValue(child, value)

												'		Case value.Contains(filepath & "\ati.ace\core-static;")
												'			value = value.Replace(filepath & "\ati.ace\core-static;", "")
												'			regkey.SetValue(child, value)

												'	End Select
											End If
										End If
									Next
								End If
							End Using
						End If
					Next
				End If
			End Using

			logEntry.Message &= Environment.NewLine & ">> Completed!"
		Catch ex As Exception
			logEntry.Message &= Environment.NewLine & ">> Failed!"
			logEntry.AddException(ex, False)
		Finally
			Application.Log.Add(logEntry)
		End Try

		'end system environement patch cleanup
	End Sub

	Private Function Checkamdkmpfd() As Boolean
		Try
			Application.Log.AddMessage("Checking if AMDKMPFD is present before Service removal")

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\CurrentControlSet\Enum\ACPI")
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						If StrContainsAny(child, True, "pnp0a08", "pnp0a03") Then
							Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
								If regkey2 IsNot Nothing Then
									For Each child2 As String In regkey2.GetSubKeyNames()
										If IsNullOrWhitespace(child2) Then Continue For

										Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey2, child2)
											If regkey3 IsNot Nothing Then
												Dim array As String() = TryCast(regkey3.GetValue("LowerFilters"), String())

												If array IsNot Nothing AndAlso array.Length > 0 Then
													For Each value As String In array
														If Not IsNullOrWhitespace(value) Then
															If StrContainsAny(value, True, "amdkmpfd") Then
																Application.Log.AddMessage("Found an AMDKMPFD! in " + child)
																Application.Log.AddMessage("We do not remove the AMDKMPFP service yet")

																Return True
															End If
														End If
													Next
												End If
											End If
										End Using

									Next
								End If
							End Using
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Return False
	End Function

	Private Sub Checkpcieroot(ByVal config As ThreadSettings)   'This is for Nvidia Optimus to prevent the yellow mark on the PCI-E controler. We must remove the UpperFilters.
		Dim win10 As Boolean = frmMain.win10
		If WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.ReleaseToken()
		End If
		UpdateTextMethod(UpdateTextTranslated(7))



		Try
			Application.Log.AddMessage("Starting the removal of nVidia Optimus UpperFilter if present.")
			Dim found As List(Of SetupAPI.Device) = SetupAPI.GetDevices("system", Nothing, False)
			If found.Count > 0 Then
				For Each d As SetupAPI.Device In found
					If StrContainsAny(d.HardwareIDs(0), True, "VEN_8086") Then
						If d.UpperFilters IsNot Nothing AndAlso d.UpperFilters.Length > 0 AndAlso StrContainsAny(d.UpperFilters(0), True, "nvpciflt", "nvkflt") Then
							If d.OemInfs.Length > 0 AndAlso (Not IsNullOrWhitespace(d.OemInfs(0).ToString)) AndAlso FileIO.ExistsFile(d.OemInfs(0).ToString) Then
								SetupAPI.UpdateDeviceInf(d, d.OemInfs(0).ToString, True)
							Else
								If win10 Then
									SetupAPI.UpdateDeviceInf(d, config.Paths.WinDir + "inf\PCI.inf", True)
								Else
									SetupAPI.UpdateDeviceInf(d, config.Paths.WinDir + "inf\machine.inf", True)
								End If
							End If
						End If
					End If
				Next
			End If
			Application.Log.AddMessage("SetupAPI removal of nVidia Optimus UpperFilter if present. Completed.")
		Catch ex As Exception
			Application.Log.AddException(ex)
			'MessageBox.Show(Languages.GetTranslation("frmMain", "Messages", "Text6"), config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error)
		End Try

		UpdateTextMethod(UpdateTextTranslated(28))
		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If
	End Sub

	Private Sub Cleannvidiaserviceprocess(ByVal config As ThreadSettings)

		Application.Log.AddMessage("Cleaning Process/Services...")

		If FileIO.ExistsFile(config.Paths.AppBase & "settings\NVIDIA\services.cfg") Then
			CleanupEngine.Cleanserviceprocess(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\services.cfg"))
		Else
			Microsoft.VisualBasic.MsgBox(config.Paths.AppBase & "settings\NVIDIA\services.cfg does not exist. please reinstall DDU", MsgBoxStyle.Critical)
		End If


		If config.RemoveGFE Then
			If FileIO.ExistsFile(config.Paths.AppBase & "settings\NVIDIA\gfeservice.cfg") Then
				CleanupEngine.Cleanserviceprocess(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\gfeservice.cfg"))
			Else
				Microsoft.VisualBasic.MsgBox(config.Paths.AppBase & "settings\NVIDIA\gfeservice.cfg does not exist. please reinstall DDU", MsgBoxStyle.Critical)
			End If
		End If

		'kill process NvTmru.exe and special kill for Logitech Keyboard(Lcore.exe) 
		'holding files in the NVIDIA folders sometimes.
		'10-10-2016 (removed dwm.exe from the list because of issues in win10 IB 14942 Wagnard)
		Try
			KillProcess(
			 "Lcore",
			 "nvgamemonitor",
			 "nvstreamsvc",
			 "NvTmru",
			 "nvxdsync",
			 "WWAHost",
			 "nvspcaps64",
			 "nvspcaps",
			 "NVIDIA Web Helper",
			 "NvBackend")

			If config.RemoveGFE Then
				KillProcess("nvtray")
			End If

		Catch ex As Exception
		End Try
		Application.Log.AddMessage("Process/Services CleanUP Complete")
	End Sub

	Private Sub old_Temporarynvidiaspeedup(ByVal config As ThreadSettings)   'we do this to speedup the removal of the nividia display driver because of the huge time the nvidia installer files take to do unknown stuff.
		Dim filePath As String = Nothing

		Try
			filePath = Environment.GetFolderPath _
			(Environment.SpecialFolder.ProgramFiles) + "\NVIDIA Corporation"

			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("installer2") Then
						For Each child2 As String In FileIO.GetDirectories(child)
							If IsNullOrWhitespace(child2) = False Then
								If child2.ToLower.Contains("display.3dvision") Or
								   child2.ToLower.Contains("display.controlpanel") Or
								   child2.ToLower.Contains("display.driver") Or
								   child2.ToLower.Contains("display.gfexperience") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("display.nvirusb") Or
								   child2.ToLower.Contains("display.optimus") Or
								   child2.ToLower.Contains("display.physx") AndAlso config.RemovePhysX Or
								   child2.ToLower.Contains("display.update") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("display.nview") Or
								   child2.ToLower.Contains("display.nvwmi") Or
								   child2.ToLower.Contains("nvdisplaycontainer") Or
								   child2.ToLower.Contains("ansel.") Or
								   child2.ToLower.Contains("gfexperience") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvab") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvidia.update") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("installer2\installer") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("network.service") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("miracast.virtualaudio") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("update.core") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("virtualaudio.driver") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("coretemp") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("shield") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("hdaudio.driver") Then
									Try
										Delete(child2)
									Catch ex As Exception
									End Try
								End If
							End If
						Next

						If FileIO.CountDirectories(child) = 0 Then
							Try
								Delete(child)
							Catch ex As Exception
							End Try
						End If
					End If
				End If
			Next
		Catch ex As Exception
		End Try
	End Sub

	Private Sub Cleannvidia(ByVal config As ThreadSettings)

		Dim wantedvalue As String = Nothing
		Dim wantedvalue2 As String = Nothing
		Dim removegfe As Boolean = config.RemoveGFE
		Dim removephysx As Boolean = config.RemovePhysX

		Dim Thread2Finished As Boolean = False
		Dim Thread3Finished As Boolean = False
		'-----------------
		'Registry Cleaning
		'-----------------
		UpdateTextMethod(UpdateTextTranslated(5))
		Application.Log.AddMessage("Starting reg cleanUP... May take a minute or two.")


		'Deleting DCOM object /classroot
		Application.Log.AddMessage("Starting dcom/clsid/appid/typelib cleanup")


		CleanupEngine.ClassRoot(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\classroot.cfg"), config)


		'Removal of the (DCH) Nvidia control panel comming from the Window Store. (In progress...)
		If win10 Then
			CleanupEngine.RemoveAppx("NVIDIAControlPanel")
		End If

		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If

		'for GFE removal only
		If removegfe Then
			Dim thread2 As Thread = New Thread(Sub() CLSIDCleanThread(Thread2Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\clsidleftoverGFE.cfg")))
			thread2.Start()
		Else
			Dim thread2 As Thread = New Thread(Sub() CLSIDCleanThread(Thread2Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\clsidleftover.cfg")))
			thread2.Start()
		End If

		Dim thread3 As Thread = New Thread(Sub() InstallerCleanThread(Thread3Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\packages.cfg"), config))
		thread3.Start()

		'------------------------------
		'Clean the rebootneeded message
		'------------------------------
		Try

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If Not IsNullOrWhitespace(child) Then
							If child.ToLower.Contains("nvidia_rebootneeded") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'-----------------
		'interface cleanup
		'-----------------
		timer.Interval = 500
		timer.AutoReset = True
		While Thread2Finished <> True
			timer.Start()
			objAuto.WaitOne()
			Application.Log.AddMessage("Waiting for MainRegCleanThread")
		End While


		If removegfe Then 'When removing GFE only
			CleanupEngine.Interfaces(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\interfaceGFE.cfg")) '// add each line as String Array.
		Else
			CleanupEngine.Interfaces(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\interface.cfg"))  '// add each line as String Array.
		End If

		Application.Log.AddMessage("Finished dcom/clsid/appid/typelib/interface cleanup")

		'end of deleting dcom stuff
		Application.Log.AddMessage("Pnplockdownfiles region cleanUP")

		CleanupEngine.Pnplockdownfiles(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\driverfiles.cfg"))  '// add each line as String Array.

		If removegfe Then
			CleanupEngine.Pnplockdownfiles(IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\gfedriverfiles.cfg")) '// add each line as String Array.
		End If
		'Cleaning PNPRessources.  'Will fix this later, its not efficent clean at all. (Wagnard)
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Khronos", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Khronos")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\NVIDIA Corporation\Global", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\NVIDIA Corporation\global")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR\SOFTWARE\NVIDIA Corporation\global", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR\SOFTWARE\NVIDIA Corporation\global")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR\SOFTWARE\NVIDIA Corporation", False)
			If regkey IsNot Nothing Then
				If regkey.SubKeyCount = 0 Then
					Try
						Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR\SOFTWARE\NVIDIA Corporation")
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Else
					For Each data As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
					Next
				End If
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\NVIDIA Corporation", False)
			If regkey IsNot Nothing Then
				If regkey.SubKeyCount = 0 Then
					Try
						Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\NVIDIA Corporation")
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Else
					For Each data As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
					Next
				End If
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Controls Folder\Display\shellex\PropertySheetHandlers\NVIDIA CPL Extension", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Controls Folder\Display\shellex\PropertySheetHandlers\NVIDIA CPL Extension")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\NVIDIA Corporation", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\NVIDIA Corporation")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using


		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\services\nvlddmkm", False)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SYSTEM\CurrentControlSet\services\nvlddmkm")
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		If IntPtr.Size = 8 Then
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\Khronos", False)
				If regkey IsNot Nothing Then
					Try
						Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKLM\SOFTWARE\Wow6432Node\Khronos")
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				End If
			End Using
		End If



		If removegfe Then
			'----------------------
			'Firewall entry cleanup
			'----------------------
			Application.Log.AddMessage("Firewall entry cleanUP")
			Try
				If winxp = False Then
					Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
						If subregkey IsNot Nothing Then
							For Each child2 As String In subregkey.GetSubKeyNames()
								If IsNullOrWhitespace(child2) Then Continue For
								If StrContainsAny(child2, True, "controlset") Then
									Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", True)
										If regkey IsNot Nothing Then
											For Each child As String In regkey.GetValueNames()
												If IsNullOrWhitespace(child) Then Continue For

												wantedvalue = regkey.GetValue(child, String.Empty).ToString()
												If IsNullOrWhitespace(wantedvalue) Then Continue For
												If StrContainsAny(wantedvalue, True, "nvstreamsrv", "nvidia network service", "nvidia update core", "NvContainer") Then
													Try
														Deletevalue(regkey, child)
													Catch ex As Exception
													End Try
												End If
											Next
										End If
									End Using
								End If
							Next
						End If
					End Using
				End If
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If
		'--------------------------
		'End Firewall entry cleanup
		'--------------------------
		Application.Log.AddMessage("End Firewall CleanUP")
		'--------------------------
		'Power Settings CleanUP
		'--------------------------
		Application.Log.AddMessage("Power Settings Cleanup")
		Try
			If winxp = False Then
				Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
					If subregkey IsNot Nothing Then
						For Each child2 As String In subregkey.GetSubKeyNames()
							If IsNullOrWhitespace(child2) Then Continue For

							If child2.ToLower.Contains("controlset") Then
								Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Control\Power\PowerSettings", True)
									If regkey IsNot Nothing Then
										For Each childs As String In regkey.GetSubKeyNames()
											If IsNullOrWhitespace(childs) Then Continue For

											Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, childs)
												If regkey2 IsNot Nothing Then
													For Each child As String In regkey2.GetValueNames()
														If IsNullOrWhitespace(child) Then Continue For

														If StrContainsAny(child, True, "description") Then
															wantedvalue = regkey2.GetValue(child, String.Empty).ToString()
															If IsNullOrWhitespace(wantedvalue) Then Continue For

															If StrContainsAny(wantedvalue, True, "nvsvc") Then
																Try
																	Deletesubregkey(regkey, childs)
																	Continue For
																Catch ex As Exception
																	Application.Log.AddException(ex)
																End Try
															End If
															If StrContainsAny(wantedvalue, True, "video and display power management") Then
																Using subregkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, childs, True)
																	If subregkey2 IsNot Nothing Then
																		For Each childinsubregkey2 As String In subregkey2.GetSubKeyNames()
																			If IsNullOrWhitespace(childinsubregkey2) Then Continue For
																			Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(subregkey2, childinsubregkey2)
																				If regkey3 IsNot Nothing Then
																					For Each childinsubregkey2value As String In regkey3.GetValueNames()
																						If IsNullOrWhitespace(childinsubregkey2value) Then Continue For

																						If childinsubregkey2value.ToString.ToLower.Contains("description") Then
																							wantedvalue2 = regkey3.GetValue(childinsubregkey2value, String.Empty).ToString
																							If IsNullOrWhitespace(wantedvalue2) Then Continue For

																							If wantedvalue2.ToString.ToLower.Contains("nvsvc") Then
																								Try
																									Deletesubregkey(subregkey2, childinsubregkey2)
																								Catch ex As Exception
																								End Try
																							End If
																						End If
																					Next
																				End If
																			End Using
																		Next
																	End If
																End Using
															End If
														End If
													Next
												End If
											End Using
										Next
									End If
								End Using
							End If
						Next
					End If
				End Using
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'--------------------------
		'End Power Settings CleanUP
		'--------------------------
		Application.Log.AddMessage("End Power Settings Cleanup")

		'--------------------------------
		'System environement path cleanup
		'--------------------------------


		If removephysx Then
			Application.Log.AddMessage("System environement CleanUP")
			Try
				Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
					If subregkey IsNot Nothing Then
						For Each child2 As String In subregkey.GetSubKeyNames()
							If IsNullOrWhitespace(child2) Then Continue For

							If child2.ToLower.Contains("controlset") Then
								Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM\" & child2 & "\Control\Session Manager\Environment", True)
									If regkey IsNot Nothing Then
										For Each child As String In regkey.GetValueNames()
											If IsNullOrWhitespace(child) Then Continue For

											If StrContainsAny(child, True, "Path") Then
												wantedvalue = regkey.GetValue(child, String.Empty).ToString.ToLower
												If IsNullOrWhitespace(wantedvalue) Then Continue For

												Try
													Select Case True
														Case StrContainsAny(wantedvalue, True, sysdrv & "program files (x86)\nvidia corporation\physx\common;")
															wantedvalue = wantedvalue.Replace(sysdrv.ToLower & "program files (x86)\nvidia corporation\physx\common;", "")
															Try
																regkey.SetValue(child, wantedvalue)
															Catch ex As Exception
																Application.Log.AddException(ex)
															End Try
														Case StrContainsAny(wantedvalue, True, ";" & sysdrv & "program files (x86)\nvidia corporation\physx\common")
															wantedvalue = wantedvalue.Replace(";" & sysdrv.ToLower & "program files (x86)\nvidia corporation\physx\common", "")
															Try
																regkey.SetValue(child, wantedvalue)
															Catch ex As Exception
																Application.Log.AddException(ex)
															End Try
													End Select
												Catch ex As Exception
													Application.Log.AddException(ex)
												End Try
											End If
										Next
									End If
								End Using
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
			Application.Log.AddMessage("End System environement path cleanup")
		End If
		'-------------------------------------
		'end system environement patch cleanup
		'-------------------------------------


		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			  "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", True)
				If regkey IsNot Nothing Then
					wantedvalue = regkey.GetValue("AppInit_DLLs", String.Empty).ToString   'Will need to consider the comma in the future for multiple value
					If IsNullOrWhitespace(wantedvalue) = False Then
						Select Case True
							Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL, " & sysdrv.ToUpper & "PROGRA~1\NVIDIA~1\NVSTRE~1\rxinput.dll")
								wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL, " & sysdrv.ToUpper & "PROGRA~1\NVIDIA~1\NVSTRE~1\rxinput.dll", "")
								regkey.SetValue("AppInit_DLLs", wantedvalue)

							Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL")
								wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL", "")
								regkey.SetValue("AppInit_DLLs", wantedvalue)

							Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~1\NVIDIA~1\NVSTRE~1\rxinput.dll")
								wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~1\NVIDIA~1\NVSTRE~1\rxinput.dll", "")
								regkey.SetValue("AppInit_DLLs", wantedvalue)
						End Select
					End If
				End If
				If regkey.GetValue("AppInit_DLLs", String.Empty).ToString = "" Then
					Try
						regkey.SetValue("LoadAppInit_DLLs", "0", RegistryValueKind.DWord)
					Catch ex As Exception
					End Try
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			If IntPtr.Size = 8 Then
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				   "SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Windows", True)

					If regkey IsNot Nothing Then
						wantedvalue = regkey.GetValue("AppInit_DLLs", String.Empty).ToString
						If IsNullOrWhitespace(wantedvalue) = False Then
							Select Case True
								Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL, " & sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\NVSTRE~1\rxinput.dll")
									wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL, " & sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\NVSTRE~1\rxinput.dll", "")
									regkey.SetValue("AppInit_DLLs", wantedvalue)

								Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL")
									wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\3DVISI~1\NVSTIN~1.DLL", "")
									regkey.SetValue("AppInit_DLLs", wantedvalue)

								Case wantedvalue.Contains(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\NVSTRE~1\rxinput.dll")
									wantedvalue = wantedvalue.Replace(sysdrv.ToUpper & "PROGRA~2\NVIDIA~1\NVSTRE~1\rxinput.dll", "")
									regkey.SetValue("AppInit_DLLs", wantedvalue)
							End Select
						End If
					End If
					If regkey.GetValue("AppInit_DLLs", String.Empty).ToString = "" Then
						Try
							regkey.SetValue("LoadAppInit_DLLs", "0", RegistryValueKind.DWord)
						Catch ex As Exception
						End Try
					End If
				End Using
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If config.RemoveVulkan Then
			CleanVulkan(config)
		End If

		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If Not IsNullOrWhitespace(users) Then
					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software", True)
						If regkey IsNot Nothing Then
							For Each child As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(child) Then Continue For

								If StrContainsAny(child, True, "nvidia corporation") Then
									Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
										If regkey2 IsNot Nothing Then
											For Each child2 As String In regkey2.GetSubKeyNames()
												If IsNullOrWhitespace(child2) Then Continue For

												If StrContainsAny(child2, True, "global") Then
													If removegfe Then
														Try
															Deletesubregkey(regkey2, child2)
														Catch ex As Exception
														End Try
													Else
														Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey, child + "\" + child2, True)
															If regkey3 IsNot Nothing Then
																For Each child3 As String In regkey3.GetSubKeyNames()
																	If IsNullOrWhitespace(child3) Then Continue For
																	If StrContainsAny(child3, True, "gfeclient", "gfexperience", "shadowplay", "ledvisualizer") Then
																		'do nothing
																	Else
																		Try
																			Deletesubregkey(regkey3, child3)
																		Catch ex As Exception
																		End Try
																	End If
																Next
															End If
														End Using
													End If
												End If
												If child2.ToLower.Contains("logging") Or
											 child2.ToLower.Contains("nvbackend") AndAlso removegfe Or
											 child2.ToLower.Contains("nvidia update core") AndAlso removegfe Or
											 child2.ToLower.Contains("nvcontrolpanel2") Or
											 child2.ToLower.Contains("nvcontrolpanel") Or
											 child2.ToLower.Contains("nvcamera") Or
											 child2.ToLower.Contains("nvtray") AndAlso removegfe Or
											 child2.ToLower.Contains("ansel") AndAlso removegfe Or
											 child2.ToLower.Contains("nvcontainer") AndAlso removegfe Or
											 child2.ToLower.Contains("nvstream") AndAlso removegfe Or
											 child2.ToLower.Contains("nvidia control panel") Then
													Try
														Deletesubregkey(regkey2, child2)
													Catch ex As Exception
													End Try
												End If
											Next
											If regkey2.SubKeyCount = 0 Then
												Try
													Deletesubregkey(regkey, child)
												Catch ex As Exception
												End Try
											Else
												For Each data As String In regkey2.GetSubKeyNames()
													If IsNullOrWhitespace(data) Then Continue For
													Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
												Next
											End If
										End If
									End Using
								End If
							Next
						End If
					End Using

					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\SOFTWARE\Microsoft\Windows\CurrentVersion\UFH\SHC", True)
						If regkey IsNot Nothing Then
							For Each child As String In regkey.GetValueNames()
								If IsNullOrWhitespace(child) Then Continue For

								Dim tArray() As String = CType(regkey.GetValue(child), String())
								If tArray.Length > 0 Then
									For Each arrayelement As String In tArray
										If IsNullOrWhitespace(arrayelement) Then Continue For

										If Not arrayelement = "" Then
											If StrContainsAny(arrayelement, True, "nvstview.exe", "vulkaninfo", "nvstlink.exe") Then
												Try
													Deletevalue(regkey, child)
												Catch ex As Exception
												End Try
											End If
											If StrContainsAny(arrayelement, True, "geforce experience") AndAlso config.RemoveGFE Then
												Try
													Deletevalue(regkey, child)
												Catch ex As Exception
												End Try
											End If
										End If
									Next
								End If
							Next
						End If
					End Using
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\UFH\ARP", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetValueNames()
					If IsNullOrWhitespace(child) Then Continue For

					Dim tArray() As String = CType(regkey.GetValue(child), String())
					If tArray.Length > 0 Then
						For Each arrayelement As String In tArray
							If IsNullOrWhitespace(arrayelement) Then Continue For

							If Not arrayelement = "" Then
								If StrContainsAny(arrayelement, True, "nvi2.dll", "vulkaninfo", "nvstlink.exe", "nvidiastereo") Then
									Try
										Deletevalue(regkey, child)
									Catch ex As Exception
									End Try
								End If
							End If
						Next
					End If
				Next
			End If
		End Using

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				 "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For

							Try
								Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
									If regkey2 IsNot Nothing Then
										If removephysx Then
											If Not IsNullOrWhitespace(regkey2.GetValue("DisplayName", String.Empty).ToString) Then
												If regkey2.GetValue("DisplayName").ToString.ToLower.Contains("physx") Then
													Deletesubregkey(regkey, child)
													Continue For
												End If
											End If
										End If
									End If
								End Using
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
							If child.ToLower.Contains("display.3dvision") Or
							 child.ToLower.Contains("3dtv") Or
							 child.ToLower.Contains("_display.controlpanel") Or
							 child.ToLower.Contains("_display.driver") Or
							 child.ToLower.Contains("_display.gfexperience") AndAlso removegfe Or
							 child.ToLower.Contains("_display.nvirusb") Or
							 child.ToLower.Contains("_display.physx") AndAlso removephysx Or
							 child.ToLower.Contains("_display.update") AndAlso removegfe Or
							 child.ToLower.Contains("_display.gamemonitor") AndAlso removegfe Or
							 child.ToLower.Contains("_gfexperience") AndAlso removegfe Or
							 child.ToLower.Contains("_hdaudio.driver") Or
							 child.ToLower.Contains("_installer") AndAlso removegfe Or
							 child.ToLower.Contains("_network.service") AndAlso removegfe Or
							 child.ToLower.Contains("_shadowplay") AndAlso removegfe Or
							 child.ToLower.Contains("_update.core") AndAlso removegfe Or
							 child.ToLower.Contains("nvidiastereo") Or
							 child.ToLower.Contains("_displaydriveranalyzer") Or
							 child.ToLower.Contains("_shieldwireless") AndAlso removegfe Or
							 child.ToLower.Contains("miracast.virtualaudio") AndAlso removegfe Or
							 child.ToLower.Contains("_nvdisplaypluginwatchdog") AndAlso removegfe Or
							 child.ToLower.Contains("_nvdisplaysessioncontainer") AndAlso removegfe Or
							 child.ToLower.Contains("_virtualaudio.driver") AndAlso removegfe Then
								If removephysx = False And child.ToLower.Contains("physx") Then
									Continue For
								End If
								If config.Remove3DTVPlay = False And child.ToLower.Contains("3dtv") Then
									Continue For
								End If
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If


		Try
			Dim mustnotremove As Boolean = False
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			 "Software\Microsoft\Windows\CurrentVersion\Uninstall", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
							If regkey2 IsNot Nothing Then
								Try
									If removephysx Then
										If IsNullOrWhitespace(regkey2.GetValue("DisplayName", String.Empty).ToString) = False Then
											If StrContainsAny(regkey2.GetValue("DisplayName", String.Empty).ToString, True, "physx") Then
												Deletesubregkey(regkey, child)
												Continue For
											End If
										End If
									End If
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						End Using

						If child.ToLower.Contains("display.3dvision") Or
						 child.ToLower.Contains("3dtv") Or
						 child.ToLower.Contains("_display.controlpanel") Or
						 child.ToLower.Contains("_display.driver") Or
						 child.ToLower.Contains("_display.optimus") Or
						 child.ToLower.Contains("_display.gfexperience") AndAlso removegfe Or
						 child.ToLower.Contains("_display.nvirusb") Or
						 child.ToLower.Contains("_nvabhub") AndAlso removegfe Or
						 child.ToLower.Contains("_display.physx") Or
						 child.ToLower.Contains("_display.update") AndAlso removegfe Or
						 child.ToLower.Contains("_osc") AndAlso removegfe Or
						 child.ToLower.Contains("_display.nview") Or
						 child.ToLower.Contains("_display.nvwmi") Or
						 child.ToLower.Contains("_display.gamemonitor") AndAlso removegfe Or
						 child.ToLower.Contains("_nvidia.update") AndAlso removegfe Or
						 child.ToLower.Contains("_gfexperience") AndAlso removegfe Or
						 child.ToLower.Contains("_hdaudio.driver") Or
						 child.ToLower.Contains("_network.service") AndAlso removegfe Or
						 child.ToLower.Contains("_shadowplay") AndAlso removegfe Or
						 child.ToLower.Contains("_update.core") AndAlso removegfe Or
						 child.ToLower.Contains("nvidiastereo") Or
						 child.ToLower.Contains("_usbc") Or
						 child.ToLower.Contains("_ansel") Or
						 child.ToLower.Contains("_shieldwireless") AndAlso removegfe Or
						 child.ToLower.Contains("miracast.virtualaudio") AndAlso removegfe Or
						 child.ToLower.Contains("_virtualaudio.driver") AndAlso removegfe Or
						 child.ToLower.Contains("vulkanrt1.") AndAlso config.RemoveVulkan Or
						 child.ToLower.Contains("_nvnodejs") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_nvbackend") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_nvplugin") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_nvtelemetry") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_ngxcore") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_nvvhci") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("_nvdisplaycontainer") Or
						 child.ToLower.Contains("_displaydriveranalyzer") Or
						 child.ToLower.Contains("_nvdisplaypluginwatchdog") AndAlso removegfe Or
						 child.ToLower.Contains("_nvdisplaysessioncontainer") AndAlso removegfe Or
						 child.ToLower.Contains("_osc") AndAlso removegfe Or
						 child.ToLower.Contains("_nvcontainer") AndAlso config.RemoveGFE Then
							If removephysx = False AndAlso child.ToLower.Contains("physx") Then
								Continue For
							End If

							If config.Remove3DTVPlay = False AndAlso child.ToLower.Contains("3dtv") Then
								Continue For
							End If
							Try
								Deletesubregkey(regkey, child)
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
						End If
					Next
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For
						If StrContainsAny(child, True, "B2FE1952-0186-46C3-BAEC-A80AA35AC5B8") AndAlso Not StrContainsAny(child, True, "_installer") Then
							mustnotremove = True
						End If
					Next
					If Not mustnotremove Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							If StrContainsAny(child, True, "_installer") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, ".DEFAULT\Software", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For

					If StrContainsAny(child, True, "nvidia corporation") Then
						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
							If regkey2 IsNot Nothing Then

								For Each child2 As String In regkey2.GetSubKeyNames()
									If IsNullOrWhitespace(child2) Then Continue For

									If StrContainsAny(child2, True, "global", "nvbackend", "nvcontrolpanel2", "nvidia control panel") Or
									  (StrContainsAny(child2, True, "nvidia update core", "nvcontainer") AndAlso removegfe) Then

										Try
											Deletesubregkey(regkey2, child2)
										Catch ex As Exception
											Application.Log.AddException(ex)
										End Try
									End If
								Next

								If regkey2.SubKeyCount = 0 Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								Else
									For Each data As String In regkey2.GetSubKeyNames()
										If IsNullOrWhitespace(data) Then Continue For
										Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
									Next
								End If
							End If
						End Using
					End If
				Next
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For

					If StrContainsAny(child, True, "ageia technologies") AndAlso removephysx Then

						Try
							Deletesubregkey(regkey, child)
						Catch ex As Exception
							Application.Log.AddException(ex)
						End Try

					End If
					If StrContainsAny(child, True, "nvidia corporation") Then
						Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
							If regkey2 IsNot Nothing Then

								For Each child2 As String In regkey2.GetSubKeyNames()
									If IsNullOrWhitespace(child2) Then Continue For

									If StrContainsAny(child2, True, "global") Then
										If removegfe Then
											Try
												Deletesubregkey(regkey2, child2)
											Catch ex As Exception
												Application.Log.AddException(ex)
											End Try
										Else
											Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey2, child2, True)
												If regkey3 IsNot Nothing Then

													For Each child3 As String In regkey3.GetSubKeyNames()
														If IsNullOrWhitespace(child3) Then Continue For

														If StrContainsAny(child3, True, "gfeclient", "gfexperience", "nvbackend", "nvscaps", "shadowplay", "ledvisualizer", "nvUpdate", "nvcontainer") Then
															'do nothing
														Else
															Try
																Deletesubregkey(regkey3, child3)
															Catch ex As Exception
																Application.Log.AddException(ex)
															End Try
														End If
													Next
												End If
											End Using
										End If
									End If
									If StrContainsAny(child2, True, "installer", "logging", "nvidia update core", "nvcontrolpanel", "nvcontrolpanel2", "physx_systemsoftware", "physxupdateloader", "uxd", "nvidia updatus") OrElse
									(StrContainsAny(child2, True, "nvstream", "nvtray", "nvcontainer", "nvdisplay.container") AndAlso removegfe) Then

										Select Case Not removephysx AndAlso StrContainsAny(child2, True, "physx")
											Case True
												'Do nothing
											Case False

												If StrContainsAny(child2, True, "installer2") AndAlso config.RemoveGFE Then
													Using regkey4 As RegistryKey = MyRegistry.OpenSubKey(regkey2, child2, True)
														If regkey4 IsNot Nothing Then
															For Each subkeys In regkey4.GetSubKeyNames
																If IsNullOrWhitespace(subkeys) Then Continue For
																If StrContainsAny(subkeys, True, "configs", "cache", "extensions", "relationships", "stripped", "drivers") Then
																	Using regkey5 As RegistryKey = MyRegistry.OpenSubKey(regkey4, subkeys, True)
																		If regkey5 IsNot Nothing Then
																			For Each ValueNames As String In regkey5.GetValueNames
																				If IsNullOrWhitespace(ValueNames) Then Continue For
																				If StrContainsAny(ValueNames, True, "ansel", "display", "gfexperience", "hdaudio", "nvabhub", "nvbackend", "nvcontainer", "nvnode", "nvplugin",
																		"nvtelemetry", "nvvhci", "osc", "shadowplay", "shieldwirelesscontroller", "update.core", "virtualaudio", "oem", "msvcruntime") Then
																					Try
																						Deletevalue(regkey5, ValueNames)
																					Catch ex As Exception
																						Application.Log.AddException(ex)
																					End Try
																				End If
																			Next
																			If regkey5.ValueCount = 0 Then
																				Try
																					Deletesubregkey(regkey4, subkeys)
																				Catch ex As Exception
																					Application.Log.AddException(ex)
																				End Try
																			End If
																		End If
																	End Using
																End If
																If StrContainsAny(subkeys, True, "pending") Then
																	Try
																		Deletesubregkey(regkey4, subkeys)
																	Catch ex As Exception
																		Application.Log.AddException(ex)
																	End Try
																End If
															Next
															If regkey4.SubKeyCount = 0 Then
																Deletesubregkey(regkey2, child2)
															End If
														End If
													End Using
												Else
													Try
														Deletesubregkey(regkey2, child2)
													Catch ex As Exception
														Application.Log.AddException(ex)
													End Try
												End If

										End Select
									End If
								Next
								If regkey2.SubKeyCount = 0 Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								Else
									For Each data As String In regkey2.GetSubKeyNames()
										If IsNullOrWhitespace(data) Then Continue For
										Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
									Next
								End If
							End If
						End Using
					End If
				Next
			End If
		End Using


		If IntPtr.Size = 8 Then
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) Then Continue For

						If StrContainsAny(child, True, "ageia technologies") Then
							If removephysx Then
								Deletesubregkey(regkey, child)
							End If
						End If
						If StrContainsAny(child, True, "nvidia corporation") Then
							Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
								If regkey2 IsNot Nothing Then

									For Each child2 As String In regkey2.GetSubKeyNames()
										If IsNullOrWhitespace(child2) Then Continue For

										If StrContainsAny(child2, True, "global") Then
											If removegfe Then
												Try
													Deletesubregkey(regkey2, child2)
												Catch ex As Exception
												End Try
											Else
												Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey2, child2, True)
													If regkey3 IsNot Nothing Then
														For Each child3 As String In regkey3.GetSubKeyNames()
															If IsNullOrWhitespace(child3) Then Continue For

															If StrContainsAny(child3, True, "gfeclient", "gfexperience", "nvbackend", "nvscaps", "shadowplay", "ledvisualizer") Then
																'do nothing
															Else
																Try
																	Deletesubregkey(regkey3, child3)
																Catch ex As Exception
																End Try
															End If
														Next
													End If
												End Using
											End If
										End If
										If StrContainsAny(child2, True, "logging", "physx_systemsoftware", "physxupdateloader", "installer2", "physx", "nvnetworkservice", "installer") Then
											If removephysx Then
												Try
													Deletesubregkey(regkey2, child2)
												Catch ex As Exception
												End Try
											Else
												If child2.ToLower.Contains("physx") Then
													'do nothing
												Else
													Try
														Deletesubregkey(regkey2, child2)
													Catch ex As Exception
													End Try
												End If
											End If
										End If
										If StrContainsAny(child2, True, "nvcontainer") AndAlso config.RemoveGFE Then
											Try
												Deletesubregkey(regkey2, child2)
											Catch ex As Exception
											End Try
										End If
									Next
									If regkey2.SubKeyCount = 0 Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
									Else
										For Each data As String In regkey2.GetSubKeyNames()
											If IsNullOrWhitespace(data) Then Continue For
											Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey2.ToString + "\ --> " + data)
										Next
									End If
								End If
							End Using
						End If
					Next
				End If
			End Using
		End If

		Using regkey = MyRegistry.OpenSubKey(Registry.CurrentUser,
		 "Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetValueNames()
					If IsNullOrWhitespace(child) Then Continue For
					If StrContainsAny(child, True, "gfexperience.exe") AndAlso removegfe Then
						Deletevalue(regkey, child)
					End If
				Next
			End If
		End Using


		Using regkey = MyRegistry.OpenSubKey(Registry.CurrentUser,
		 "Software\Microsoft\.NETFramework\SQM\Apps", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For
					If StrContainsAny(child, True, "gfexperience.exe") AndAlso removegfe Then
						Deletesubregkey(regkey, child)
					End If
				Next
			End If
		End Using

		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If IsNullOrWhitespace(users) Then Continue For
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users,
					 users + "\Software\Microsoft\.NETFramework\SQM\Apps", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For
							If child.ToLower.Contains("gfexperience.exe") AndAlso removegfe Then
								Deletesubregkey(regkey, child)
							End If
						Next
					End If
				End Using
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If IsNullOrWhitespace(users) Then Continue For
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users,
					 users + "\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames()
							If IsNullOrWhitespace(child) Then Continue For
							If StrContainsAny(child, True, "gfexperience.exe", "GeForce Experience.exe") AndAlso removegfe Then
								Deletevalue(regkey, child)
							End If
						Next
					End If
				End Using
			Next

		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
		 "Software\Microsoft\Windows NT\CurrentVersion\ProfileList", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For
					Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
						"Software\Microsoft\Windows NT\CurrentVersion\ProfileList\" & child, False)
						If subregkey IsNot Nothing Then
							If Not IsNullOrWhitespace(subregkey.GetValue("ProfileImagePath", String.Empty).ToString) Then
								wantedvalue = subregkey.GetValue("ProfileImagePath", String.Empty).ToString
								If Not IsNullOrWhitespace(wantedvalue) Then
									If wantedvalue.Contains("UpdatusUser") Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
									End If
								End If
							End If
						End If
					End Using
				Next
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
		 "Software\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For
					Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
						 "Software\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\" & child, False)
						If subregkey IsNot Nothing Then
							If Not IsNullOrWhitespace(subregkey.GetValue("", String.Empty).ToString) Then
								wantedvalue = subregkey.GetValue("", String.Empty).ToString
								If IsNullOrWhitespace(wantedvalue) = False Then
									If wantedvalue.ToLower.Contains("nvidia control panel") Or
										   wantedvalue.ToLower.Contains("nvidia nview desktop manager") Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
										'special case only to nvidia afaik. there i a clsid for a control pannel that link from namespace.
										Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "CLSID", True)
											If regkey2 IsNot Nothing Then
												Try
													Deletesubregkey(regkey2, child)
												Catch ex As Exception
												End Try
											End If
										End Using
									End If
								End If
							End If
						End If
					End Using
				Next
			End If
		End Using

		'----------------------
		'.net ngenservice clean
		'----------------------
		Application.Log.AddMessage("ngenservice Clean")

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\.NETFramework\v2.0.50727\NGenService\Roots", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("gfexperience.exe") AndAlso removegfe Then
							Try
								Deletesubregkey(regkey, child)
							Catch ex As Exception
							End Try
						End If
					End If
				Next
			End If
		End Using
		If IntPtr.Size = 8 Then

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v2.0.50727\NGenService\Roots", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("gfexperience.exe") AndAlso removegfe Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
				End If
			End Using
		End If
		Application.Log.AddMessage("End ngenservice Clean")
		'-----------------------------
		'End of .net ngenservice clean
		'-----------------------------

		'-----------------------------
		'Mozilla plugins
		'-----------------------------
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\MozillaPlugins", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("nvidia.com/3dvision") Then
							Try
								Deletesubregkey(regkey, child)
							Catch ex As Exception
							End Try
						End If
					End If
				Next
			End If
		End Using


		If IntPtr.Size = 8 Then
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Wow6432Node\MozillaPlugins", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("nvidia.com/3dvision") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
				End If
			End Using
		End If


		'-----------------------
		'remove event view stuff
		'-----------------------
		Application.Log.AddMessage("Remove eventviewer stuff")

		Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
			If subregkey IsNot Nothing Then
				For Each child2 As String In subregkey.GetSubKeyNames()
					If IsNullOrWhitespace(child2) Then Continue For

					If child2.ToLower.Contains("controlset") Then
						Using regkey As RegistryKey = MyRegistry.OpenSubKey(subregkey, child2 & "\Services\eventlog\Application", True)
							If regkey IsNot Nothing Then
								For Each child As String In regkey.GetSubKeyNames()
									If IsNullOrWhitespace(child) Then Continue For

									If child.ToLower.StartsWith("nvidia update") Or
									 (child.ToLower.StartsWith("nvstreamsvc") AndAlso removegfe) Or
									 child.ToLower.StartsWith("nvidia opengl driver") Or
									 child.ToLower.StartsWith("nvwmi") Or
									 child.ToLower.StartsWith("nview") Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
											Application.Log.AddException(ex)
										End Try
									End If
								Next
							End If
						End Using
					End If
				Next
			End If
		End Using

		Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SYSTEM", False)
			If subregkey IsNot Nothing Then
				For Each child2 As String In subregkey.GetSubKeyNames()
					If IsNullOrWhitespace(child2) Then Continue For

					If child2.ToLower.Contains("controlset") Then
						Using regkey As RegistryKey = MyRegistry.OpenSubKey(subregkey, child2 & "\Services\eventlog\System", True)
							If regkey IsNot Nothing Then
								For Each child As String In regkey.GetSubKeyNames()
									If IsNullOrWhitespace(child) Then Continue For

									If child.ToLower.StartsWith("nvidia update") Or
									 child.ToLower.StartsWith("nvidia opengl driver") Or
									 child.ToLower.StartsWith("nvwmi") Or
									 child.ToLower.StartsWith("nvlddmkm") Or
									 child.ToLower.StartsWith("nview") Then
										Deletesubregkey(regkey, child)
									End If
								Next
							End If
						End Using
					End If
				Next
			End If
		End Using

		Application.Log.AddMessage("End Remove eventviewer stuff")
		'---------------------------
		'end remove event view stuff
		'---------------------------


		'-----------------------
		'Windows Error Reporting
		'-----------------------

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For
					If StrContainsAny(child, True, "nvcontainer.exe", "nvidia geforce experience", "nvnodejslauncher", "nvidia share.exe", "nvidia web helper.exe", "nvidia.steamlauncher.exe", "nvoawrappercache.exe", "nvprofileupdater", "nvshim", "nvsphelper", "nvstreamer", "nvtelemetrycontainer", "nvtmmon", "nvtmrep", "oawrapper") AndAlso removegfe Then
						Try
							Deletesubregkey(regkey, child)
						Catch ex As Exception
							Application.Log.AddException(ex, "Windows error Reporting (LocalDumps)")
						End Try
					End If
				Next
			End If
		End Using


		'---------------------------
		'virtual store
		'---------------------------

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "VirtualStore\MACHINE\SOFTWARE\NVIDIA Corporation", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "Global")
				Catch ex As Exception
				End Try
				If regkey.SubKeyCount = 0 Then
					Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "VirtualStore\MACHINE\SOFTWARE", True)
						If regkey2 IsNot Nothing Then
							Try
								Deletesubregkey(regkey2, "NVIDIA Corporation")
							Catch ex As Exception
							End Try
						End If
					End Using
				Else
					For Each data As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
					Next
				End If
			End If
		End Using

		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If Not IsNullOrWhitespace(users) Then
					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software\Classes\VirtualStore\MACHINE\SOFTWARE\NVIDIA Corporation", True)
						If regkey IsNot Nothing Then
							Try
								Deletesubregkey(regkey, "Global")
							Catch ex As Exception
							End Try
							If regkey.SubKeyCount = 0 Then
								Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software\Classes\VirtualStore\MACHINE\SOFTWARE", True)
									If regkey2 IsNot Nothing Then
										Try
											Deletesubregkey(regkey2, "NVIDIA Corporation")
										Catch ex As Exception
										End Try
									End If
								End Using
							Else
								For Each data As String In regkey.GetSubKeyNames()
									If IsNullOrWhitespace(data) Then Continue For
									Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
								Next
							End If
						End If
					End Using
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			For Each child As String In Registry.Users.GetSubKeyNames()
				If IsNullOrWhitespace(child) Then Continue For
				If StrContainsAny(child, True, "s-1-5") Then
					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, child & "Software\Classes\VirtualStore\MACHINE\SOFTWARE\NVIDIA Corporation", True)
						If regkey IsNot Nothing Then
							Try
								Deletesubregkey(regkey, "Global")
								If regkey.SubKeyCount = 0 Then
									Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, child & "Software\Classes\VirtualStore\MACHINE\SOFTWARE", True)
										If regkey2 IsNot Nothing Then
											Try
												Deletesubregkey(regkey2, "NVIDIA Corporation")
											Catch ex As Exception
											End Try
										End If
									End Using
								Else
									For Each data As String In regkey.GetSubKeyNames()
										If IsNullOrWhitespace(data) Then Continue For
										Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
									Next
								End If
							Catch ex As Exception
							End Try
						End If
					End Using
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "SOFTWARE\NVIDIA Corporation", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "Global")
				Catch ex As Exception
				End Try
				If regkey.SubKeyCount = 0 Then
					Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "SOFTWARE", True)
						If regkey2 IsNot Nothing Then
							Try
								Deletesubregkey(regkey2, "NVIDIA Corporation")
								Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR\SOFTWARE\NVIDIA Corporation")
							Catch ex As Exception
							End Try
						End If
					End Using
				Else
					For Each data As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
					Next
				End If
			End If
		End Using

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			"Software\Microsoft\Windows\CurrentVersion\Run", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames
						If IsNullOrWhitespace(child) Then Continue For
						If StrContainsAny(child, True, "nvtmru", "NvCplDaemon", "NvMediaCenter", "NvBackend", "nwiz", "ShadowPlay", "StereoLinksInstall", "NvGameMonitor") Then
							Deletevalue(regkey, child)
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			If IntPtr.Size = 8 Then
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
				 "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames
							If IsNullOrWhitespace(child) Then Continue For
							If StrContainsAny(child, True, "StereoLinksInstall") Then
								Deletevalue(regkey, child)
							End If
						Next
					End If
				End Using
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		If config.Remove3DTVPlay Then
			Try
				Deletesubregkey(Registry.ClassesRoot, "mpegfile\shellex\ContextMenuHandlers\NvPlayOnMyTV")
			Catch ex As Exception
			End Try
			Try
				Deletesubregkey(Registry.ClassesRoot, "WMVFile\shellex\ContextMenuHandlers\NvPlayOnMyTV")
			Catch ex As Exception
			End Try
			Try
				Deletesubregkey(Registry.ClassesRoot, "AVIFile\shellex\ContextMenuHandlers\NvPlayOnMyTV")
			Catch ex As Exception
			End Try
		End If

		'-----------------------------
		'Shell extensions\approved
		'-----------------------------
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) Then Continue For
						If regkey.GetValue(child).ToString.ToLower.Contains("nvcpl desktopcontext class") Or
							   regkey.GetValue(child).ToString.ToLower.Contains("nview desktop context menu") Or
							   regkey.GetValue(child).ToString.ToLower.Contains("nvappshext extension") Or
							   regkey.GetValue(child).ToString.ToLower.Contains("openglshext extension") Or
							   regkey.GetValue(child).ToString.ToLower.Contains("nvidia play on my tv context menu extension") Then
							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
							End Try
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Controls Folder\" &
		  "Display\shellex\PropertySheetHandlers", True)
			If regkey IsNot Nothing Then
				Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, "NVIDIA CPL Extension", True)
					If subregkey IsNot Nothing Then
						wantedvalue = subregkey.GetValue("NVIDIA CPL Extension", String.Empty).ToString
						If Not IsNullOrWhitespace(wantedvalue) Then
							Using regkey2 As RegistryKey = Registry.Users
								If regkey2 IsNot Nothing Then
									For Each child As String In regkey2.GetSubKeyNames
										If IsNullOrWhitespace(child) Then Continue For
										Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(regkey2, child & "\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached", True)
											If regkey3 IsNot Nothing Then
												For Each valuename As String In regkey3.GetValueNames
													If IsNullOrWhitespace(valuename) Then Continue For
													If StrContainsAny(valuename, True, wantedvalue) Then
														Try
															Deletevalue(regkey3, valuename)
														Catch exARG As ArgumentException
															'nothing to do,it probably doesn't exit.
														Catch ex As Exception
															Application.Log.AddException(ex)
														End Try
													End If
												Next
											End If
										End Using
									Next
								End If
							End Using
						End If
					End If
				End Using
				Try
					Deletesubregkey(regkey, "NVIDIA CPL Extension")
				Catch exARG As ArgumentException
					'nothing to do,it probably doesn't exit.
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "SOFTWARE\Microsoft\Windows\CurrentVersion\Controls Folder\" &
		  "Display\shellex\PropertySheetHandlers", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "NVIDIA CPL Extension")
				Catch exARG As ArgumentException
					'nothing to do,it probably doesn't exit.
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Extended Properties", False)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames()
					If IsNullOrWhitespace(child) Then Continue For

					Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)
						If regkey2 IsNot Nothing Then
							For Each childs As String In regkey2.GetValueNames()
								If IsNullOrWhitespace(childs) Then Continue For

								If StrContainsAny(childs, True, "nvcpl.cpl") Then
									Try
										Deletevalue(regkey2, childs)
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								End If
							Next
						End If
					End Using
				Next
			End If
		End Using

		If IntPtr.Size = 8 Then

			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) Then Continue For

						If StrContainsAny(regkey.GetValue(child, String.Empty).ToString, False, "nvcpl desktopcontext class") Then
							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
							End Try
						End If
					Next
				End If
			End Using
		End If
		'-----------------------------
		'End Shell extensions\aprouved
		'-----------------------------

		'Shell ext
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Directory\background\shellex\ContextMenuHandlers", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "NvCplDesktopContext")
				Catch ex As Exception
				End Try

				Try
					Deletesubregkey(regkey, "00nView")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Classes\Directory\background\shellex\ContextMenuHandlers", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "NvCplDesktopContext")
				Catch ex As Exception
				End Try

				Try
					Deletesubregkey(regkey, "00nView")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, ".avi\shellex", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "{3D1975AF-0FC3-463d-8965-4DC6B5A840F4}")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, ".mpe\shellex", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "{3D1975AF-0FC3-463d-8965-4DC6B5A840F4}")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, ".mpeg\shellex", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "{3D1975AF-0FC3-463d-8965-4DC6B5A840F4}")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, ".mpg\shellex", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "{3D1975AF-0FC3-463d-8965-4DC6B5A840F4}")
				Catch ex As Exception
				End Try
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, ".wmv\shellex", True)
			If regkey IsNot Nothing Then
				Try
					Deletesubregkey(regkey, "{3D1975AF-0FC3-463d-8965-4DC6B5A840F4}")
				Catch ex As Exception
				End Try
			End If
		End Using

		'Cleaning of some "open with application" related to 3d vision
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "jpsfile\shell\open\command", True)
			If regkey IsNot Nothing Then
				If (Not IsNullOrWhitespace(regkey.GetValue("", String.Empty).ToString)) AndAlso StrContainsAny(regkey.GetValue("", String.Empty).ToString, True, "nvstview") Then
					Try
						Deletesubregkey(Registry.ClassesRoot, "jpsfile")
					Catch ex As Exception
					End Try
				End If
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "mpofile\shell\open\command", True)
			If regkey IsNot Nothing Then
				If (Not IsNullOrWhitespace(regkey.GetValue("", String.Empty).ToString)) AndAlso StrContainsAny(regkey.GetValue("", String.Empty).ToString, True, "nvstview") Then
					Try
						Deletesubregkey(Registry.ClassesRoot, "mpofile")
					Catch ex As Exception
					End Try
				End If
			End If
		End Using

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "pnsfile\shell\open\command", True)
			If regkey IsNot Nothing Then
				If (Not IsNullOrWhitespace(regkey.GetValue("", String.Empty).ToString)) AndAlso StrContainsAny(regkey.GetValue("", String.Empty).ToString, True, "nvstview") Then
					Try
						Deletesubregkey(Registry.ClassesRoot, "pnsfile")
					Catch ex As Exception
					End Try
				End If
			End If
		End Using

		Try
			Deletesubregkey(Registry.ClassesRoot, ".tvp")  'CrazY_Milojko
		Catch ex As Exception
		End Try

		'Task Scheduler cleanUP 
		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetSubKeyNames
					If IsNullOrWhitespace(child) Then Continue For
					Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
						If regkey2 IsNot Nothing Then
							If Not IsNullOrWhitespace(regkey2.GetValue("Description", String.Empty).ToString) Then
								If StrContainsAny(regkey2.GetValue("Description", String.Empty).ToString, True, "nvprofileupdater", "nvnodelauncher", "nvtmmon", "nvtmrep", "NvDriverUpdateCheckDaily", "NVIDIA GeForce Experience", "NVIDIA Profile Updater", "NVIDIA telemetry monitor", "NVIDIA crash and telemetry reporter", "batteryboost", "nvngx") AndAlso config.RemoveGFE Then
									Deletesubregkey(regkey, child)
								End If
							End If
						End If
					End Using
				Next
			End If
		End Using

		Using schedule As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache", True)
			If schedule IsNot Nothing Then
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(schedule, "Tree", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames
							If IsNullOrWhitespace(child) Then Continue For
							If StrContainsAny(child, True, "nvprofileupdater", "nvnodelauncher", "nvtmmon", "nvtmrep", "NvDriverUpdateCheckDaily", "NVIDIA GeForce Experience", "NvBatteryBoostCheckOnLogon", "nvngx") AndAlso config.RemoveGFE Then
								For Each ScheduleChild As String In schedule.GetSubKeyNames
									If IsNullOrWhitespace(ScheduleChild) Then Continue For
									Try
										Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, child)
											If regkey2 IsNot Nothing Then
												If Not IsNullOrWhitespace(regkey2.GetValue("Id", String.Empty).ToString) Then
													wantedvalue = regkey2.GetValue("Id", String.Empty).ToString
													Using regkey3 As RegistryKey = MyRegistry.OpenSubKey(schedule, ScheduleChild, True)
														If regkey3 IsNot Nothing Then
															For Each child2 As String In regkey3.GetSubKeyNames
																If IsNullOrWhitespace(child2) Then Continue For
																If StrContainsAny(wantedvalue, True, child2) Then
																	Deletesubregkey(regkey3, child2)
																End If
															Next
														End If
													End Using
												End If
											End If
										End Using
									Catch ex As Exception
										Application.Log.AddException(ex)
									End Try
								Next
								Deletesubregkey(regkey, child)
							End If
						Next
					End If
				End Using
			End If
		End Using

		'      Dim OldValue As String = Nothing
		'      Select Case System.Windows.Forms.SystemInformation.BootMode
		'          Case Forms.BootMode.FailSafe
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If

		'          Case Forms.BootMode.FailSafeWithNetwork
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If
		'          Case Forms.BootMode.Normal
		'              'Usually this service is Running in normal mode, we *could* in the future check all this.
		'              If (CheckServiceStartupType("Schedule")) <> "4" Then
		'                  StartService("Schedule")
		'              Else
		'                  OldValue = CheckServiceStartupType("Schedule")
		'                  SetServiceStartupType("Schedule", "3")
		'                  StartService("Schedule")
		'              End If
		'      End Select

		'Using tsc As New TaskSchedulerControl(config)
		'	For Each task As Task In tsc.GetAllTasks
		'		If StrContainsAny(task.Name, True, "nvprofileupdater", "nvnodelauncher", "nvtmmon", "nvtmrep", "NvDriverUpdateCheckDaily", "NVIDIA GeForce Experience") AndAlso config.RemoveGFE Then
		'			Try
		'				task.Delete()
		'			Catch ex As Exception
		'				Application.Log.AddException(ex)
		'			End Try
		'			Application.Log.AddMessage("TaskScheduler: " & task.Name & " as been removed")
		'		End If
		'	Next
		'End Using

		'      Select Case System.Windows.Forms.SystemInformation.BootMode
		'          Case Forms.BootMode.FailSafe
		'              StopService("Schedule")
		'              If OldValue IsNot Nothing Then
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'          Case Forms.BootMode.FailSafeWithNetwork
		'              StopService("Schedule")
		'              If OldValue IsNot Nothing Then
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'          Case Forms.BootMode.Normal
		'              'Usually this service is running in normal mode, we don't need to stop it.
		'              If OldValue IsNot Nothing Then
		'                  StopService("Schedule")
		'                  SetServiceStartupType("Schedule", OldValue)
		'              End If
		'      End Select
		timer.Interval = 500
		timer.AutoReset = False
		While Thread2Finished <> True Or Thread3Finished <> True
			timer.Start()
			objAuto.WaitOne()
			Application.Log.AddMessage("Waiting for InstallerCleanThread")
		End While

		UpdateTextMethod("End of Registry Cleaning")

		Application.Log.AddMessage("End of Registry Cleaning")

		'Killing Explorer.exe to help releasing file that were open.
		Application.Log.AddMessage("Killing Explorer.exe")
		KillProcess("explorer")

	End Sub

	Private Sub Cleannvidiafolders(ByVal config As ThreadSettings)
		Dim filePath As String = Nothing
		Dim removephysx As Boolean = config.RemovePhysX
		Dim Thread1Finished = False
		Dim Thread2Finished = False


		Dim thread1 As Thread = New Thread(Sub() Threaddata1(Thread1Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\driverfiles.cfg")))
		thread1.Start()

		If config.RemoveGFE Then
			Dim thread2 As Thread = New Thread(Sub() Threaddata1(Thread2Finished, IO.File.ReadAllLines(config.Paths.AppBase & "settings\NVIDIA\gfedriverfiles.cfg")))
			thread2.Start()
		Else
			Thread2Finished = True
		End If


		'Delete NVIDIA data Folders
		'Here we delete the Geforce experience / Nvidia update user it created. This fail sometime for no reason :/

		UpdateTextMethod(UpdateTextTranslated(3))
		Application.Log.AddMessage("Cleaning UpdatusUser users ac if present")

		Dim AD As DirectoryEntry = New DirectoryEntry("WinNT://" + Environment.MachineName.ToString())
		Dim users As DirectoryEntries = AD.Children
		Dim newuser As DirectoryEntry = Nothing

		Try
			newuser = users.Find("UpdatusUser")
			users.Remove(newuser)
		Catch ex As Exception
		End Try

		UpdateTextMethod(UpdateTextTranslated(4))

		Application.Log.AddMessage("Cleaning Directory")


		If config.RemoveNvidiaDirs = True Then
			filePath = sysdrv + "NVIDIA"

			Delete(filePath)


		End If

		' here I erase the folders / files of the nvidia GFE / update in users.
		filePath = config.Paths.UserPath
		For Each child As String In FileIO.GetDirectories(filePath)
			If IsNullOrWhitespace(child) = False Then
				If StrContainsAny(child, True, "updatususer") Then

					Delete(child)

					Delete(child)


					'Yes we do it 2 times. This will workaround a problem on junction/sybolic/hard link
					'(Will have to see if this is still valid. This was on old driver pre 300.xx I believe :/ )
					Delete(child)

					Delete(child)

				End If
			End If
		Next

		filePath = config.Paths.UserPath + "Public\Desktop"
		If FileIO.ExistsDir(filePath) Then
			If filePath IsNot Nothing Then
				For Each child As String In FileIO.GetFiles(filePath)
					If IsNullOrWhitespace(child) = False Then
						If (StrContainsAny(child, True, "geforce experience.lnk") AndAlso config.RemoveGFE) Or
							(StrContainsAny(child, True, "3d vision photo viewer")) Then

							Delete(child)

						End If
					End If
				Next
			End If
		End If

		filePath = config.Paths.UserPath + "Public\Pictures\NVIDIA Corporation"
		If FileIO.ExistsDir(filePath) Then
			If filePath IsNot Nothing Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If StrContainsAny(child, True, "3d vision experience") Then

							Delete(child)

						End If
					End If
				Next
				Try
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			End If
		End If

		filePath = config.Paths.System32 + "drivers\NVIDIA Corporation"
		If FileIO.ExistsDir(filePath) Then
			If filePath IsNot Nothing Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If StrContainsAny(child, True, "drs") Then

							Delete(child)

						End If
					End If
				Next
				Try
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			End If
		End If

		For Each filepaths As String In FileIO.GetDirectories(config.Paths.UserPath)
			If IsNullOrWhitespace(filepaths) Then Continue For
			filePath = filepaths + "\AppData\Local\NVIDIA"


			Try
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If (child.ToLower.Contains("nvbackend") AndAlso config.RemoveGFE) Or
						 (child.ToLower.Contains("nvosc.") AndAlso config.RemoveGFE) Or
						 (child.ToLower.Contains("shareconnect") AndAlso config.RemoveGFE) Or
						 (child.ToLower.Contains("nvgs") AndAlso config.RemoveGFE) Or
						 (child.ToLower.Contains("glcache") AndAlso config.RemoveGFE) Or
						 (child.ToLower.Contains("gfexperience") AndAlso config.RemoveGFE) Then

							Delete(child)

						End If
					End If
				Next
				Try
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			Catch ex As Exception
			End Try


			filePath = filepaths + "\AppData\Roaming\NVIDIA"

			Try
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("computecache") Or
						 child.ToLower.Contains("glcache") Then

							Delete(child)

						End If
					End If
				Next
				Try
					If FileIO.CountDirectories(filePath) = 0 Then

						Delete(filePath)

					Else
						For Each data As String In FileIO.GetDirectories(filePath)
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
						Next

					End If
				Catch ex As Exception
				End Try
			Catch ex As Exception
			End Try


			filePath = filepaths + "\AppData\Local\NVIDIA Corporation"
			If config.RemoveGFE Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If (child.ToLower.Contains("ledvisualizer") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvab") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("gfexperience") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("geforce experience") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvnode") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvtmmon") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvprofileupdater") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvstreamsrv") AndAlso config.RemoveGFE) Or
							 (child.ToLower.EndsWith("\osc") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvvad") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvidia share") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvidia notification") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvfbc") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvtmrep") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvtelemetry") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("gfesdk") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvdriverupdatecheck") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvbatteryboostcheck") AndAlso config.RemoveGFE) Or
							 (child.ToLower.Contains("nvetwlog")) Or
							 (child.ToLower.Contains("shield apps") AndAlso config.RemoveGFE) Then


								Delete(child)

							End If
						End If
					Next
					Try
						If FileIO.CountDirectories(filePath) = 0 Then

							Delete(filePath)

						Else
							For Each data As String In FileIO.GetDirectories(filePath)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
							Next

						End If
					Catch ex As Exception
					End Try
				Catch ex As Exception
				End Try
			End If

		Next

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\NVIDIA"

		Try
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("updatus") Or
					 child.ToLower.Contains("shimgen") Or
					 (child.ToLower.Contains("ngx") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("grid") AndAlso config.RemoveGFE) Then

						Delete(child)

					End If
				End If
			Next
			Try
				If FileIO.CountDirectories(filePath) = 0 AndAlso config.RemoveGFE Then

					Delete(filePath)

				Else
					For Each data As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
					Next
				End If
			Catch ex As Exception
			End Try
		Catch ex As Exception
		End Try

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\NVIDIA Corporation"
		Try
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("drs") Or
					 child.ToLower.Contains("nv_cache") Or
					 child.ToLower.Contains("umdlogs") Or
					 (child.ToLower.Contains("geforce experience") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvab") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("gfexperience") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("netservice") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("crashdumps") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvstream") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("downloader") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("gfebridges") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("ledvisualizer") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nview") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvfbc") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvstapisvr") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvtelemetry") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvstereoinstaller") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvvad") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("driverdumps") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvbackend") AndAlso config.RemoveGFE) Or
					 (child.ToLower.Contains("nvstreamsvc") AndAlso config.RemoveGFE) Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		Catch ex As Exception
		End Try

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Microsoft\Windows\Start Menu\Programs\NVIDIA Corporation"
		Try
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("3d vision") Then

						Delete(child)

					End If
					If child.ToLower.Contains("geforce experience") AndAlso config.RemoveGFE Then

						Delete(child)

					End If
				End If
			Next
			Try
				If FileIO.CountDirectories(filePath) = 0 AndAlso (FileIO.CountFiles(filePath) = 0 AndAlso config.RemoveGFE) Then

					Delete(filePath)

				Else
					For Each data As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
					Next
				End If
			Catch ex As Exception
			End Try
		Catch ex As Exception
		End Try

		filePath = Environment.GetFolderPath _
		(Environment.SpecialFolder.ProgramFiles) + "\NVIDIA Corporation"
		If FileIO.ExistsDir(filePath) Then
			Dim hit As Boolean = False
			For Each child As String In FileIO.GetDirectories(filePath)
				hit = False
				If IsNullOrWhitespace(child) = False Then
					If child.ToLower.Contains("control panel client") Or
					   child.ToLower.Contains("display") Or
					   child.ToLower.Contains("coprocmanager") Or
					   child.ToLower.Contains("drs") Or
					   child.ToLower.Contains("nvsmi") Or
					   child.ToLower.Contains("opencl") Or
					   child.ToLower.Contains("ansel") Or
					   child.ToLower.Contains("3d vision") Or
					   child.ToLower.Contains("led visualizer") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvab") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("netservice") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("geforce experience") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvstreamc") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvstreamsrv") AndAlso config.RemoveGFE Or
					   child.ToLower.EndsWith("\physx") AndAlso config.RemovePhysX Or
					   child.ToLower.Contains("nvstreamsrv") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvfbc") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("update common") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("shield") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nview") Or
					   child.ToLower.Contains("nvidia wmi provider") Or
					   child.ToLower.Contains("gamemonitor") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvcontainer") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvbackend") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvtelemetry") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvidia ngx") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvdriverupdatecheck") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvdlisr") AndAlso config.RemoveGFE Or
					   child.ToLower.Contains("nvgsync") Or
					   child.ToLower.Contains("nvupdate") Or
					   child.ToLower.Contains("update core") AndAlso config.RemoveGFE Then

						Delete(child)

					End If
					If child.ToLower.Contains("installer2") Then
						For Each child2 As String In FileIO.GetDirectories(child)
							If IsNullOrWhitespace(child2) = False Then
								If child2.ToLower.Contains("display.3dvision") Or
								   child2.ToLower.Contains("display.controlpanel") Or
								   child2.ToLower.Contains("display.driver") Or
								   child2.ToLower.Contains("displaydriveranalyzer") Or
								   child2.ToLower.Contains("display.optimus") Or
								   child2.ToLower.Contains("msvcruntime") Or
								   child2.ToLower.Contains("ansel.") Or
								   child2.ToLower.Contains("nvdisplaycontainer") Or
								   child2.ToLower.Contains("display.gfexperience") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvab") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("osc.") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("osclib.") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("display.nvirusb") Or
								   child2.ToLower.Contains("display.physx") AndAlso config.RemovePhysX Or
								   child2.ToLower.Contains("display.update") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("display.gamemonitor") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("gfexperience") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvidia.update") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("installer2\installer") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("network.service") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("miracast.virtualaudio") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("update.core") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("virtualaudio.driver") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("coretemp") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("shield") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvcontainer") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvnodejs") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvplugin") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvdisplaypluginwatchdog") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvdisplaysessioncontainer") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvbackend") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvtelemetry") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("nvvhci") AndAlso config.RemoveGFE Or
								   child2.ToLower.Contains("hdaudio.driver") AndAlso config.RemoveGFE Then


									'This registry check is for protection to prevent removal of CUDA (or other) Nvidia uninstall association.
									Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine,
			 "Software\Microsoft\Windows\CurrentVersion\Uninstall", True)
										If regkey IsNot Nothing Then
											For Each childs As String In regkey.GetSubKeyNames()
												If IsNullOrWhitespace(childs) Then Continue For

												Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(regkey, childs)
													If regkey2 IsNot Nothing Then
														If removephysx Then
															If IsNullOrWhitespace(regkey2.GetValue("NVI2_Package", String.Empty).ToString) = False Then
																If StrContainsAny(regkey2.GetValue("NVI2_Package", String.Empty).ToString, True, child2) Then

																	hit = True
																End If
															End If
															If IsNullOrWhitespace(regkey2.GetValue("UninstallString_Hidden", String.Empty).ToString) = False Then
																If StrContainsAny(regkey2.GetValue("UninstallString_Hidden", String.Empty).ToString, True, child2) Then

																	hit = True
																End If
															End If
															If IsNullOrWhitespace(regkey2.GetValue("NVI2_Setup", String.Empty).ToString) = False Then
																If StrContainsAny(regkey2.GetValue("NVI2_Setup", String.Empty).ToString, True, child2) Then

																	hit = True
																End If
															End If
														End If
													End If
												End Using

											Next
										End If
									End Using

									If Not hit Then
										Delete(child2)
									End If

								End If
							End If
						Next

						If FileIO.CountDirectories(child) = 0 Then

							Delete(child)

						Else
							For Each data As String In FileIO.GetDirectories(child)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + child + "\ --> " + data)
							Next

						End If
					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next
			End If
		End If


		If config.RemovePhysX Then
			filePath = Environment.GetFolderPath _
			 (Environment.SpecialFolder.ProgramFiles) + "\AGEIA Technologies"
			If FileIO.ExistsDir(filePath) Then

				Delete(filePath)

			End If
		End If


		If IntPtr.Size = 8 Then
			filePath = config.Paths.ProgramFilesx86 & "NVIDIA Corporation"
			If FileIO.ExistsDir(filePath) Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("3d vision") Or
						 child.ToLower.Contains("coprocmanager") Or
						 child.ToLower.Contains("led visualizer") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvab") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("osc") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("netservice") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvidia geforce experience") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvstreamc") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvstreamsrv") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvfbc") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("update common") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("display.nvcontainer") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvcontainer") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvbackend") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvnode") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("shadowplay") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("nvgsync") Or
						 child.ToLower.Contains("nvidia updatus") Or
						 child.ToLower.EndsWith("\physx") AndAlso config.RemovePhysX Or
						 child.ToLower.EndsWith("nvtelemetry") AndAlso config.RemoveGFE Or
						 child.ToLower.Contains("update core") AndAlso config.RemoveGFE Then
							If removephysx Then

								Delete(child)

							Else
								If child.ToLower.Contains("physx") Then
									'do nothing
								Else

									Delete(child)

								End If
							End If
						End If
					End If
				Next

				If FileIO.CountDirectories(filePath) = 0 Then

					Delete(filePath)

				Else
					For Each data As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
					Next

				End If
			End If
		End If


		If config.RemovePhysX Then
			If IntPtr.Size = 8 Then
				filePath = Environment.GetFolderPath _
				 (Environment.SpecialFolder.ProgramFiles) + " (x86)" + "\AGEIA Technologies"
				If FileIO.ExistsDir(filePath) Then

					Delete(filePath)

				End If
			End If
		End If

		filePath = config.Paths.System32
		Dim files() As String = IO.Directory.GetFiles(filePath, "nvdisp*.*")
		For i As Integer = 0 To files.Length - 1
			If Not IsNullOrWhitespace(files(i)) Then

				Delete(files(i))

			End If
		Next

		filePath = config.Paths.System32
		files = IO.Directory.GetFiles(filePath, "nvhdagenco*.*")
		For i As Integer = 0 To files.Length - 1
			If Not IsNullOrWhitespace(files(i)) Then

				Delete(files(i))

			End If
		Next

		filePath = config.Paths.WinDir
		Try
			Delete(filePath + "Help\nvcpl")
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			filePath = config.Paths.WinDir + "Temp"
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If StrContainsAny(child, True, "NVIDIA Corporation", "NvidiaLogging") Then
						Delete(child)
					End If
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			filePath = config.Paths.SystemDrive & "Temp"
			If FileIO.ExistsDir(filePath) Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If StrContainsAny(child, True, "NVIDIA") Then
							Delete(child)
						End If
					End If
				Next
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try


		For Each filepaths As String In FileIO.GetDirectories(config.Paths.UserPath)
			If IsNullOrWhitespace(filepaths) Then Continue For
			filePath = filepaths + "\AppData\Local\Temp\NvidiaLogging"
			If FileIO.ExistsDir(filePath) AndAlso config.RemoveGFE Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then

							Delete(child)

						End If
					Next
					Try
						If FileIO.CountDirectories(filePath) = 0 Then

							Delete(filePath)

						Else
							For Each data As String In FileIO.GetDirectories(filePath)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
							Next

						End If
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If

			filePath = filepaths + "\AppData\Local\Temp\NVIDIA Corporation"
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("nv_cache") Or
							 child.ToLower.Contains("displaydriver") Then

								Delete(child)

							End If
						End If
					Next
					Try
						If FileIO.CountDirectories(filePath) = 0 Then

							Delete(filePath)

						Else
							For Each data As String In FileIO.GetDirectories(filePath)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
							Next

						End If
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If

			filePath = filepaths + "\AppData\Local\Temp\NVIDIA"
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If (child.ToLower.Contains("geforceexperienceselfupdate") AndAlso config.RemoveGFE) Or
							  (child.ToLower.Contains("gfe") AndAlso config.RemoveGFE) Or
							   child.ToLower.Contains("displaydriver") Then

								Delete(child)

							End If
						End If
					Next
					Try
						If FileIO.CountDirectories(filePath) = 0 Then

							Delete(filePath)

						Else
							For Each data As String In FileIO.GetDirectories(filePath)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
							Next

						End If
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If

			filePath = filepaths + "\AppData\Local\Temp\Low\NVIDIA Corporation"
			If FileIO.ExistsDir(filePath) Then
				Try
					For Each child As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("nv_cache") Then

								Delete(child)

							End If
						End If
					Next
					Try
						If FileIO.CountDirectories(filePath) = 0 Then

							Delete(filePath)

						Else
							For Each data As String In FileIO.GetDirectories(filePath)
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
							Next

						End If
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				Catch ex As Exception
					Application.Log.AddException(ex)
				End Try
			End If
			'windows 8+ only (store apps nv_cache cleanup)

			Try
				If win8higher Then
					Dim prefilePath As String = filepaths + "\AppData\Local\Packages"
					If FileIO.ExistsDir(prefilePath) Then
						For Each childs As String In FileIO.GetDirectories(prefilePath)
							If Not IsNullOrWhitespace(childs) Then
								filePath = childs + "\AC\Temp\NVIDIA Corporation"

								If FileIO.ExistsDir(filePath) Then
									For Each child As String In FileIO.GetDirectories(filePath)
										If IsNullOrWhitespace(child) = False Then
											If child.ToLower.Contains("nv_cache") Then

												Delete(child)

											End If
										End If
									Next

									If FileIO.CountDirectories(filePath) = 0 Then

										Delete(filePath)

									Else
										For Each data As String In FileIO.GetDirectories(filePath)
											If IsNullOrWhitespace(data) Then Continue For
											Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
										Next

									End If
								End If
							End If
						Next
					End If
				End If
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try

		Next

		'Cleaning the GFE 2.0.1 and earlier assemblies.
		If config.RemoveGFE Then
			filePath = Environment.GetEnvironmentVariable("windir") + "\assembly\NativeImages_v4.0.30319_32"
			If FileIO.ExistsDir(filePath) Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If child.ToLower.Contains("gfexperience") Or
						 child.ToLower.Contains("nvidia.sett") Or
						 child.ToLower.Contains("nvidia.updateservice") Or
						 child.ToLower.Contains("nvidia.win32api") Or
						 child.ToLower.Contains("installeruiextension") Or
						 child.ToLower.Contains("installerservice") Or
						 child.ToLower.Contains("gridservice") Or
						 child.ToLower.Contains("shadowplay") Or
						   child.ToLower.Contains("nvidia.gfe") Then

							Delete(child)

						End If
					End If
				Next
			End If
		End If

		'-----------------
		'MUI cache cleanUP
		'-----------------
		'Note: this MUST be done after cleaning the folders.
		Application.Log.AddMessage("MuiCache CleanUP")
		Try
			For Each regusers As String In Registry.Users.GetSubKeyNames
				If IsNullOrWhitespace(regusers) Then Continue For

				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, regusers & "\software\classes\local settings\muicache", False)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) Then Continue For

							Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, False)
								If subregkey IsNot Nothing Then
									For Each childs As String In subregkey.GetSubKeyNames()
										If IsNullOrWhitespace(childs) Then Continue For

										Using regkey2 As RegistryKey = MyRegistry.OpenSubKey(subregkey, childs, True)
											If regkey2 IsNot Nothing Then
												For Each Keyname As String In regkey2.GetValueNames
													If IsNullOrWhitespace(Keyname) Then Continue For

													If StrContainsAny(Keyname, True, "nvstlink.exe", "nvstview.exe", "nvcpluir.dll", "nvcplui.exe", "mcu.exe") Or
													 (StrContainsAny(Keyname, True, "gfexperience.exe", "nvidia share.exe") AndAlso config.RemoveGFE) Then
														Try
															Deletevalue(regkey2, Keyname)
														Catch ex As Exception
															Application.Log.AddException(ex)
														End Try
													End If
												Next
											End If
										End Using
									Next
								End If
							End Using
						Next
					End If
				End Using

				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, regusers & "\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetValueNames()
							If IsNullOrWhitespace(child) Then Continue For

							If StrContainsAny(child, True, "nvcplui.exe", "nvtray.exe") Or
							 (StrContainsAny(child, True, "nvbackend.exe") AndAlso config.RemoveGFE) Or
							 (StrContainsAny(child, True, "GeForce Experience\Update\setup.exe") AndAlso config.RemoveGFE) Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
								End Try
							End If
						Next
					End If
				End Using

			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			For Each regusers As String In Registry.Users.GetSubKeyNames
				If IsNullOrWhitespace(regusers) Then Continue For

				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, regusers & "\software\classes\local settings\software\microsoft\windows\shell\muicache", True)
					If regkey IsNot Nothing Then
						For Each Keyname As String In regkey.GetValueNames
							If IsNullOrWhitespace(Keyname) Then Continue For

							If StrContainsAny(Keyname, True, "nvcplui.exe", "nvstlink.exe", "nvstview.exe", "nvcpluir.dll") Or
							   (StrContainsAny(Keyname, True, "gfexperience.exe", "nvidia share.exe") AndAlso config.RemoveGFE) Then
								Try
									Deletevalue(regkey, Keyname)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						Next
					End If
				End Using
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		timer.Interval = 500
		timer.AutoReset = False
		While Thread1Finished <> True Or Thread2Finished <> True
			timer.Start()
			objAuto.WaitOne()
		End While
	End Sub

	Private Sub cleanintel(ByVal config As ThreadSettings)

		Dim wantedvalue As String = Nothing
		Dim packages As String()

		UpdateTextMethod(UpdateTextTranslated(5))

		Application.Log.AddMessage("Cleaning registry")

		'Removal of the (DCH) from the Window Store. (In progress...)
		If win10 Then
			CleanupEngine.RemoveAppx("IntelGraphicsControlPanel")
		End If

		CleanupEngine.Pnplockdownfiles(IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\driverfiles.cfg")) '// add each line as String Array.

		CleanupEngine.ClassRoot(IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\classroot.cfg"), config) '// add each line as String Array.

		CleanupEngine.Interfaces(IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\interface.cfg")) '// add each line as String Array.

		CleanupEngine.Clsidleftover(IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\clsidleftover.cfg")) '// add each line as String Array.

		If config.RemoveVulkan Then
			CleanVulkan(config)
		End If

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Intel", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "display", "igd", "gfx", "mediasdk", "opencl", "intel wireless display", "kmd", "mdf") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(MyRegistry.OpenSubKey(Registry.LocalMachine, "Software", True), "Intel")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			For Each users As String In Registry.Users.GetSubKeyNames()
				If Not IsNullOrWhitespace(users) Then
					Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.Users, users & "\Software\Intel", True)
						If regkey IsNot Nothing Then
							For Each child As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(child) = False Then
									If child.ToLower.Contains("display") Then
										Try
											Deletesubregkey(regkey, child)
										Catch ex As Exception
										End Try
									End If
								End If
							Next
							If regkey.SubKeyCount = 0 Then
								Try
									Deletesubregkey(MyRegistry.OpenSubKey(Registry.Users, users & "\Software", True), "Intel")
								Catch ex As Exception
								End Try
							Else
								For Each data As String In regkey.GetSubKeyNames()
									If IsNullOrWhitespace(data) Then Continue For
									Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
								Next
							End If
						End If
					End Using
				End If
			Next
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		If IntPtr.Size = 8 Then
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node\Intel", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then
								If StrContainsAny(child, True, "display", "igd", "gfx", "mediasdk", "opencl", "intel wireless display", "mdf") Then
									Try
										Deletesubregkey(regkey, child)
									Catch ex As Exception
									End Try
								End If
							End If
						Next
						If regkey.SubKeyCount = 0 Then
							Try
								Deletesubregkey(MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node", True), "Intel")
							Catch ex As Exception
							End Try
						Else
							For Each data As String In regkey.GetSubKeyNames()
								If IsNullOrWhitespace(data) Then Continue For
								Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
							Next
						End If
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If


		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Microsoft\Windows\CurrentVersion\Run", True)
				If regkey IsNot Nothing Then
					Try
						Deletevalue(regkey, "IgfxTray")
					Catch ex As Exception
					End Try

					Try
						Deletevalue(regkey, "Persistence")
					Catch ex As Exception
					End Try

					Try
						Deletevalue(regkey, "HotKeysCmds")
					Catch ex As Exception
					End Try
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.ClassesRoot, "Directory\background\shellex\ContextMenuHandlers", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("igfxcui") Or
							   child.ToLower.Contains("igfxosp") Or
							 child.ToLower.Contains("igfxdtcm") Then

								Deletesubregkey(regkey, child)

							End If
						End If

					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		CleanupEngine.Installer(IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\packages.cfg"), config)

		If IntPtr.Size = 8 Then
			packages = IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\packages.cfg") '// add each line as String Array.
			Try
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", True)
					If regkey IsNot Nothing Then
						For Each child As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(child) = False Then

								Using subregkey As RegistryKey = MyRegistry.OpenSubKey(regkey, child, True)

									If subregkey IsNot Nothing Then
										If Not IsNullOrWhitespace(subregkey.GetValue("DisplayName", String.Empty).ToString) Then
											wantedvalue = subregkey.GetValue("DisplayName", String.Empty).ToString
											If Not IsNullOrWhitespace(wantedvalue) Then
												For i As Integer = 0 To packages.Length - 1
													If Not IsNullOrWhitespace(packages(i)) Then
														If StrContainsAny(wantedvalue, True, packages(i)) Then
															Try
																If Not (config.RemoveVulkan = False AndAlso StrContainsAny(wantedvalue, True, "vulkan")) Then
																	Deletesubregkey(regkey, child)
																End If
															Catch ex As Exception
															End Try
														End If
													End If
												Next
											End If
										End If
									End If
								End Using
							End If
						Next
					End If
				End Using
			Catch ex As Exception
				Application.Log.AddException(ex)
			End Try
		End If
		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cpls", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If IsNullOrWhitespace(child) = False Then
							If child.ToLower.Contains("igfxcpl") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		'Special Cleanup For Intel PnpResources
		Try
			If win8higher Then
				Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpResources\Registry\HKCR", True)
					If regkey IsNot Nothing Then
						Dim classroot As String() = IO.File.ReadAllLines(config.Paths.AppBase & "settings\INTEL\classroot.cfg")
						For Each child As String In regkey.GetSubKeyNames()
							If Not IsNullOrWhitespace(child) Then
								For i As Integer = 0 To classroot.Length - 1
									If Not IsNullOrWhitespace(classroot(i)) Then
										If child.ToLower.Contains(classroot(i).ToLower) Then
											Try
												Deletesubregkey(regkey, child)
											Catch ex As Exception
											End Try
										End If
									End If
								Next
							End If
						Next
					End If
				End Using
			End If
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		Try
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notify", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetSubKeyNames()
						If Not IsNullOrWhitespace(child) Then
							If child.ToLower.Contains("igfx") Then
								Try
									Deletesubregkey(regkey, child)
								Catch ex As Exception
								End Try
							End If
						End If
					Next
					If regkey.SubKeyCount = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notify")
						Catch ex As Exception
						End Try
					Else
						For Each data As String In regkey.GetSubKeyNames()
							If IsNullOrWhitespace(data) Then Continue For
							Application.Log.AddWarningMessage("Remaining Key(s) found " + " : " + regkey.ToString + "\ --> " + data)
						Next
					End If
				End If
			End Using
		Catch ex As Exception
			Application.Log.AddException(ex)
		End Try

		UpdateTextMethod(UpdateTextTranslated(6))
		Application.Log.AddMessage("Killing Explorer.exe")

		KillProcess("explorer")
	End Sub

	Private Sub cleanintelserviceprocess()

		Application.Log.AddMessage("Cleaning Process/Services...")
		CleanupEngine.Cleanserviceprocess(IO.File.ReadAllLines(Application.Paths.AppBase & "settings\INTEL\services.cfg")) '// add each line as String Array.

		KillProcess("IGFXEM")
		Application.Log.AddMessage("Process/Services CleanUP Complete")
	End Sub

	Private Sub cleanintelfolders()

		Dim filePath As String = Nothing

		UpdateTextMethod(UpdateTextTranslated(4))

		Application.Log.AddMessage("Cleaning Directory")

		CleanupEngine.Folderscleanup(IO.File.ReadAllLines(Application.Paths.AppBase & "settings\INTEL\driverfiles.cfg"))      '// add each line as String Array.

		filePath = System.Environment.SystemDirectory
		Dim files() As String = IO.Directory.GetFiles(filePath + "\", "igfxcoin*.*")
		For i As Integer = 0 To files.Length - 1
			If Not IsNullOrWhitespace(files(i)) Then
				Try
					Delete(files(i))
				Catch ex As Exception
				End Try
			End If
		Next

		filePath = Environment.GetFolderPath _
			  (Environment.SpecialFolder.ProgramFiles) + "\Intel"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If StrContainsAny(child, True, "Media SDK", "Media Resource") Then
						Delete(child)
					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then
				Delete(filePath)
			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next
			End If
		End If

		filePath = Environment.GetFolderPath _
		 (Environment.SpecialFolder.CommonApplicationData) + "\Intel"
		If FileIO.ExistsDir(filePath) Then
			For Each child As String In FileIO.GetDirectories(filePath)
				If IsNullOrWhitespace(child) = False Then
					If StrContainsAny(child, True, "shadercache") Then

						Delete(child)

					End If
				End If
			Next
			If FileIO.CountDirectories(filePath) = 0 Then

				Delete(filePath)

			Else
				For Each data As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(data) Then Continue For
					Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
				Next

			End If
		End If

		If IntPtr.Size = 8 Then
			filePath = Application.Paths.ProgramFilesx86 + "Intel"
			If FileIO.ExistsDir(filePath) Then
				For Each child As String In FileIO.GetDirectories(filePath)
					If IsNullOrWhitespace(child) = False Then
						If StrContainsAny(child, True, "Media SDK", "Media Resource", "Intel(R) Processor Graphics") Then
							Delete(child)
						End If
					End If
				Next
				If FileIO.CountDirectories(filePath) = 0 Then
					Delete(filePath)
				Else
					For Each data As String In FileIO.GetDirectories(filePath)
						If IsNullOrWhitespace(data) Then Continue For
						Application.Log.AddWarningMessage("Remaining folders found " + " : " + filePath + "\ --> " + data)
					Next
				End If
			End If
		End If

	End Sub

	Private Sub CleanVulkan(ByRef config As ThreadSettings)

		Dim FilePath As String = Nothing
		Dim files() As String = Nothing

		Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\Khronos\OpenCL\Vendors", True)
			If regkey IsNot Nothing Then
				For Each child As String In regkey.GetValueNames()
					If IsNullOrWhitespace(child) = False Then
						If StrContainsAny(child, True, "amdocl") AndAlso config.SelectedGPU = GPUVendor.AMD Then
							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
						End If
						If StrContainsAny(child, True, "nvopencl") AndAlso config.SelectedGPU = GPUVendor.Nvidia Then
							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
						End If
						If StrContainsAny(child, True, "intelopencl") AndAlso config.SelectedGPU = GPUVendor.Intel Then
							Try
								Deletevalue(regkey, child)
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
						End If
					End If
				Next
				If regkey.GetValueNames().Length = 0 Then
					Try
						Deletesubregkey(Registry.LocalMachine, "Software\Khronos\OpenCL")
					Catch ex As Exception
						Application.Log.AddException(ex)
					End Try
				End If

				Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\Khronos", True)
					If subregkey IsNot Nothing Then
						If subregkey.GetSubKeyNames().Length = 0 Then
							Try
								Deletesubregkey(Registry.LocalMachine, "Software\Khronos")
							Catch ex As Exception
								Application.Log.AddException(ex)
							End Try
						End If
					End If
				End Using
			End If
		End Using
		If config.WinIs64 Then
			Using regkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "Software\WOW6432Node\Khronos\OpenCL\Vendors", True)
				If regkey IsNot Nothing Then
					For Each child As String In regkey.GetValueNames()
						If IsNullOrWhitespace(child) = False Then
							If StrContainsAny(child, True, "amdocl") AndAlso config.SelectedGPU = GPUVendor.AMD Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
							If StrContainsAny(child, True, "nvopencl") AndAlso config.SelectedGPU = GPUVendor.Nvidia Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
							If StrContainsAny(child, True, "intelopencl") AndAlso config.SelectedGPU = GPUVendor.Intel Then
								Try
									Deletevalue(regkey, child)
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						End If
					Next
					If regkey.GetValueNames().Length = 0 Then
						Try
							Deletesubregkey(Registry.LocalMachine, "Software\WOW6432Node\Khronos\OpenCL")
						Catch ex As Exception
							Application.Log.AddException(ex)
						End Try
					End If

					Using subregkey As RegistryKey = MyRegistry.OpenSubKey(Registry.LocalMachine, "SOFTWARE\WOW6432Node\Khronos", True)
						If subregkey IsNot Nothing Then
							If subregkey.GetSubKeyNames().Length = 0 Then
								Try
									Deletesubregkey(Registry.LocalMachine, "Software\WOW6432Node\Khronos")
								Catch ex As Exception
									Application.Log.AddException(ex)
								End Try
							End If
						End If
					End Using
				End If
			End Using
		End If

		FilePath = System.Environment.SystemDirectory
		files = IO.Directory.GetFiles(FilePath + "\", "vulkan-1*.dll")
		For i As Integer = 0 To files.Length - 1
			If Not IsNullOrWhitespace(files(i)) Then
				Try
					Delete(files(i))
				Catch ex As Exception
				End Try
			End If
		Next

		files = IO.Directory.GetFiles(FilePath + "\", "vulkaninfo*.*")
		For i As Integer = 0 To files.Length - 1
			If Not IsNullOrWhitespace(files(i)) Then
				Try
					Delete(files(i))
				Catch ex As Exception
				End Try
			End If
		Next


		If IntPtr.Size = 8 Then
			FilePath = Environment.GetEnvironmentVariable("windir") + "\SysWOW64"
			files = IO.Directory.GetFiles(FilePath + "\", "vulkan-1*.dll")
			For i As Integer = 0 To files.Length - 1
				If Not IsNullOrWhitespace(files(i)) Then
					Try
						Delete(files(i))
					Catch ex As Exception
					End Try
				End If
			Next

			files = IO.Directory.GetFiles(FilePath + "\", "vulkaninfo*.*")
			For i As Integer = 0 To files.Length - 1
				If Not IsNullOrWhitespace(files(i)) Then
					Try
						Delete(files(i))
					Catch ex As Exception
					End Try
				End If
			Next
		End If

		If config.RemoveVulkan Then
			FilePath = config.Paths.ProgramFiles + "VulkanRT"
			If FileIO.ExistsDir(FilePath) Then

				Delete(FilePath)

			End If

			If IntPtr.Size = 8 Then
				FilePath = Application.Paths.ProgramFilesx86 + "VulkanRT"
				If FileIO.ExistsDir(FilePath) Then

					Delete(FilePath)

				End If
			End If

		End If

	End Sub

	Private Sub AmdEnvironementPath(ByVal filepath As String)
		Dim valuesToFind() As String = New String() {
		 filepath & "\amd app\bin\x86_64",
		 filepath & "\amd app\bin\x86",
		 filepath & "\ati.ace\core-static"
		}

		CleanEnvironementPath(valuesToFind)
	End Sub

	Private Sub UpdateTextMethod(ByVal strMessage As String)
		frmMain.UpdateTextMethod(strMessage)
	End Sub

	Private Function UpdateTextTranslated(ByVal number As Integer) As String
		Return frmMain.UpdateTextTranslated(number)
	End Function

	Private Sub Delete(ByVal filename As String)

		FileIO.Delete(filename)
		CleanupEngine.RemoveSharedDlls(filename)
	End Sub

	Private Sub Threaddata1(ByRef ThreadFinised As Boolean, ByVal driverfiles As String())
		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If

		ThreadFinised = False
		CleanupEngine.Folderscleanup(driverfiles)
		ThreadFinised = True
	End Sub

	Private Sub Deletesubregkey(ByVal value1 As RegistryKey, ByVal value2 As String)
		CleanupEngine.Deletesubregkey(value1, value2)
	End Sub

	Private Sub Deletevalue(ByVal value1 As RegistryKey, ByVal value2 As String)
		CleanupEngine.Deletevalue(value1, value2)
	End Sub

	Private Sub CLSIDCleanThread(ByRef ThreadFinished As Boolean, ByVal Clsidleftover As String())
		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If

		ThreadFinished = False
		CleanupEngine.Clsidleftover(Clsidleftover)
		ThreadFinished = True
	End Sub

	Private Sub InstallerCleanThread(ByRef ThreadFinised As Boolean, ByVal Packages As String(), config As ThreadSettings)
		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If

		ThreadFinised = False
		CleanupEngine.Installer(Packages, config)
		ThreadFinised = True
	End Sub

	Private Sub ClassrootCleanThread(ByRef ThreadFinised As Boolean, ByVal Classroot As String(), config As ThreadSettings)
		If Not WindowsIdentity.GetCurrent().IsSystem Then
			ImpersonateLoggedOnUser.Taketoken()
			ACL.AddPriviliges(ACL.SE.SECURITY_NAME, ACL.SE.BACKUP_NAME, ACL.SE.RESTORE_NAME, ACL.SE.TAKE_OWNERSHIP_NAME, ACL.SE.TCB_NAME, ACL.SE.CREATE_TOKEN_NAME)
		End If

		ThreadFinised = False
		CleanupEngine.ClassRoot(Classroot, config)
		ThreadFinised = True
	End Sub

End Class
