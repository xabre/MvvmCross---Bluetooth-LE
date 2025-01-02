using System;
using Android.Bluetooth;
using Android.Content;
using Android.OS;

using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Android;
using Plugin.BLE.Extensions;

namespace Plugin.BLE.BroadcastReceivers;

public class BondStatusBroadcastReceiver(Adapter adapter) : BroadcastReceiver
{
	public event EventHandler<DeviceBondStateChangedEventArgs> BondStateChanged;

	public override void OnReceive(Context context, Intent intent)
	{
		if (BondStateChanged == null)
			return;

		var extraBondState = (Bond)intent.GetIntExtra(BluetoothDevice.ExtraBondState, (int)Bond.None);

		BluetoothDevice bluetoothDevice;
		
		#if NET6_0_OR_GREATER
		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		#else
		if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
		#endif
		{
			bluetoothDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice, Java.Lang.Class.FromType(typeof(BluetoothDevice)));
		}
		else
		{
			bluetoothDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
		}

		var device = new Device(adapter, bluetoothDevice, null);
		var address = bluetoothDevice?.Address ?? string.Empty;
		var bondState = extraBondState.XPlatformBondState();
		BondStateChanged(this, new() { Address = address, Device = device, State = bondState });
	}
}
