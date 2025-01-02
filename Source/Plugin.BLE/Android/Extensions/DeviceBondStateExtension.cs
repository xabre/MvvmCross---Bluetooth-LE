using System;

using Android.Bluetooth;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts.Pairing;


namespace Plugin.BLE.Extensions;

internal static class DeviceBondStateExtension
{
	public static DeviceBondState XPlatformBondState(this Bond bondState)
	{
		switch (bondState)
		{
			case Bond.None:
				return DeviceBondState.NotBonded;
			case Bond.Bonding:
				return DeviceBondState.Bonding;
			case Bond.Bonded:
				return DeviceBondState.Bonded;
			default:
				return DeviceBondState.NotSupported;
		}
	}

	internal static DeviceBondStatus XPlatformBondStatus(this Bond pairStatus)
	{
		return pairStatus switch
		{
			Bond.Bonded => DeviceBondStatus.Paired,
			Bond.Bonding => DeviceBondStatus.SpecifiedFailure,
			Bond.None => DeviceBondStatus.UnspecifiedFailure,
			_ => throw new ArgumentOutOfRangeException(nameof(pairStatus), pairStatus, null)
		};
	}
}
