using BLE.Client.WinConsole;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;


Console.WriteLine("Hello, BLE World!");
Console.WriteLine($"Environment.OSVersion.Version.Build: {Environment.OSVersion.Version.Build}");
using (var ct = new ConsoleTracer())
{

	Plugin.BLE.Abstractions.Trace.TraceImplementation = ct.GetPrefixedTrace("Plugin.BLE");
	var pdDemos = new PluginDemos(ct.GetPrefixedTrace("      DEMO"));
	var wdemos = new WindowsDemos(ct.GetPrefixedTrace("      DEMO"));
	var demoDict = new Dictionary<ConsoleKey, Demo>
	{
		{ConsoleKey.N, new("Turn Bluetooth ON", pdDemos.TurnBluetoothOn) },
		{ConsoleKey.F, new("Turn Bluetooth OFF", pdDemos.TurnBluetoothOff) },
		{ConsoleKey.D1, new("Discover and set the BleAddress", pdDemos.DiscoverAndSelect) },
		{ConsoleKey.D2, new("Set the BleAddress", BleAddressSelector.NewBleAddress) },
		{ConsoleKey.D3, new("Connect -> Disconnect", pdDemos.Connect_Disconnect) },
		{ConsoleKey.D4, new("Pair -> Connect -> Disconnect", pdDemos.Pair_Connect_Disconnect) },
		{ConsoleKey.D5, new("Connect -> Change Parameters -> Disconnect", pdDemos.Connect_Change_Parameters_Disconnect) },
		{ConsoleKey.D6, new("Run GetSystemConnectedOrPairedDevices", pdDemos.RunGetSystemConnectedOrPairedDevices) },
		{ConsoleKey.D7, new("Loop: Connect -> Read services -> Disconnect", pdDemos.Connect_Read_Services_Disconnect_Loop) },
		{ConsoleKey.D8, new("Loop: Connect -> Read services -> Dispose", pdDemos.Connect_Read_Services_Dispose_Loop) },
		{ConsoleKey.D9, new("Connect -> Loop: ConnectionLost -> Connect", pdDemos.Connect_ConnectionLost_Reconnect) },
		{ConsoleKey.Q, new("Adapter.BondAsync", pdDemos.BondAsync) },
		{ConsoleKey.W, new("Adapter.BondedDevices", pdDemos.GetBondedDevices) },
		{ConsoleKey.S, new("Device.BondState", pdDemos.ShowBondState) },
		{ConsoleKey.T, new("Pure Windows: Connect -> Disconnect", wdemos.Connect_Disconnect) },
		{ConsoleKey.U, new("Pure Windows: Unpair all BLE devices", wdemos.UnPairAllBleDevices) },

		{ConsoleKey.G, new($"{nameof(pdDemos.GetSelectedStatus)}", pdDemos.GetSelectedStatus) },
		{ConsoleKey.E, new($"{nameof(pdDemos.ConnectSelected)}", pdDemos.ConnectSelected) },
		{ConsoleKey.D, new($"{nameof(pdDemos.DisconnectSelected)}", pdDemos.DisconnectSelected) },
		{ConsoleKey.O, new($"{nameof(pdDemos.PairNone)}", pdDemos.PairNone) },
		{ConsoleKey.C, new($"{nameof(pdDemos.PairConsent)}", pdDemos.PairConsent) },
		{ConsoleKey.I, new($"{nameof(pdDemos.PairDisplayPin)}", pdDemos.PairDisplayPin) },
		{ConsoleKey.R, new($"{nameof(pdDemos.PairProvidePin)}", pdDemos.PairProvidePin) },
		{ConsoleKey.M, new($"{nameof(pdDemos.PairConfirmPinMatch)}", pdDemos.PairConfirmPinMatch) },
		{ConsoleKey.P, new($"{nameof(pdDemos.PairProvidePasswordCredential)}", pdDemos.PairProvidePasswordCredential) },
		{ConsoleKey.A, new($"{nameof(pdDemos.PairAny)}", pdDemos.PairAny) },
		{ConsoleKey.L, new($"{nameof(pdDemos.SetPairingRequestProtectionLevel)}", pdDemos.SetPairingRequestProtectionLevel) },
		{ConsoleKey.V, new($"{nameof(pdDemos.UnPairSelectedDevice)}", pdDemos.UnPairSelectedDevice) }
	};

	while (true)
	{
		Console.WriteLine();
		Console.WriteLine(BleAddressSelector.DoesBleAddressExists() ? $"Using BLE Address: {BleAddressSelector.GetBleAddress()}" : "No Ble address has been set - use key '1' or '2' to set the BLE address)");
		Console.WriteLine("List of tests to run for key:");
		Console.WriteLine();
		Console.WriteLine(ConsoleKey.Escape + ": Quit!");

		foreach (var demo in demoDict)
		{
			Console.WriteLine(demo.Key + ": " + demo.Value.Description);
		}

		var key = Console.ReadKey();
		if (key.Key == ConsoleKey.Escape)
		{
			break;
		}
		if (demoDict.TryGetValue(key.Key, out Demo? chosenDemo))
		{
			Console.WriteLine();
			Console.WriteLine($"Running: {chosenDemo.Description}");
			Console.WriteLine("-------------------------------------------------------");
			if (chosenDemo is null)
			{
				throw new("No such demo!");
			}
			await chosenDemo.Method();
		}
		else
		{
			Console.WriteLine($"{key}  -> No such test. Remember {ConsoleKey.Escape} -> Quit!");
		}
		await Task.Delay(200);
		Console.WriteLine("-------------------------------------------------------");
	}
}


