using System;

using Windows.Devices.Enumeration;

using Plugin.BLE.Abstractions.Contracts.Pairing;


namespace Plugin.BLE.Extensions;

internal static class DeviceBondStatusExtension
{
	public static DeviceBondStatus XPlatformPairStatus(this DevicePairingResultStatus pairStatus)
	{
		switch (pairStatus)
		{
			case DevicePairingResultStatus.Paired:
				return DeviceBondStatus.Paired;

			case DevicePairingResultStatus.NotPaired:
			case DevicePairingResultStatus.Failed:
				return DeviceBondStatus.UnspecifiedFailure;

			case DevicePairingResultStatus.NotReadyToPair:
			case DevicePairingResultStatus.AlreadyPaired:
			case DevicePairingResultStatus.ConnectionRejected:
			case DevicePairingResultStatus.TooManyConnections:
			case DevicePairingResultStatus.HardwareFailure:
			case DevicePairingResultStatus.AuthenticationTimeout:
			case DevicePairingResultStatus.AuthenticationNotAllowed:
			case DevicePairingResultStatus.AuthenticationFailure:
			case DevicePairingResultStatus.NoSupportedProfiles:
			case DevicePairingResultStatus.ProtectionLevelCouldNotBeMet:
			case DevicePairingResultStatus.AccessDenied:
			case DevicePairingResultStatus.InvalidCeremonyData:
			case DevicePairingResultStatus.PairingCanceled:
			case DevicePairingResultStatus.OperationAlreadyInProgress:
			case DevicePairingResultStatus.RequiredHandlerNotRegistered:
			case DevicePairingResultStatus.RejectedByHandler:
			case DevicePairingResultStatus.RemoteDeviceHasAssociation:
				return DeviceBondStatus.SpecifiedFailure;

			default: throw new ArgumentOutOfRangeException(nameof(pairStatus), pairStatus, null);
		}
	}
}
