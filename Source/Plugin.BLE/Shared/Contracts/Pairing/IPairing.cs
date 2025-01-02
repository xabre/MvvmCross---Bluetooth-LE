using System;
using System.Threading;
using System.Threading.Tasks;


namespace Plugin.BLE.Abstractions.Contracts.Pairing;

/// <summary>
/// Indicate an <see cref="IAdapter"/> is able to programmatically request device Bonding, and is compatible with Xplatform Bonding signatures and optional pairing parameters and results.
/// </summary>
public interface IBondable
{
	/// <summary>
	/// Use to create a Bond with Pairing options
	/// </summary>
	/// <param name="device"> Device to pair </param>
	/// <param name="bondingOptions"> For use when the platform's <see cref="IAdapter"/> is capable of accepting pairing options </param>
	/// <param name="cancellationToken"> To cancel the Bonding process </param>
	/// <returns></returns>
	public Task<BondResult> BondAsync(IDevice device, BondingOptions bondingOptions = null, CancellationToken cancellationToken = default);
}


/// <summary>
/// Indicate an <see cref="IAdapter"/> is compatible of handling a programmatic pairing response
/// </summary>
public interface IPairProcess
{
	public event EventHandler<PairRespondedEventArgs> PairResponded;

	/// <summary>
	/// Arguments returned from a device in response to the pairing request
	/// </summary>
	/// <param name="selectedMode"> The negotiated paring mode selected by the remote device </param>
	/// <param name="approvePairingResponse"> Method to call when the pairing response is accepted </param>
	/// <param name="pin"> Pin provided to application for verification:
	/// <see cref="PairModes.DisplayPin"/> When generated for user display and external verification,
	/// <see cref="PairModes.ConfirmPinMatch"/> When submitted by remote device for user or application verification </param>
	public class PairRespondedEventArgs(PairModes selectedMode, Action<IPairResponse> approvePairingResponse, string pin = null) : System.EventArgs
	{
		public PairModes SelectedMode { get; } = selectedMode;
		public Action<IPairResponse> ApprovePairingResponse { get; } = approvePairingResponse;
		public string Pin { get; } = pin;
	}


	/// <summary>
	/// Use to Accept the pairing response when the negotiated mode either:
	/// <see cref="PairModes.Consent"/>,
	/// <see cref="PairModes.ConfirmPinMatch"/>,
	/// </summary>
	public class ConfirmPairResponse : IPairResponse;


	/// <summary>
	/// Use to Accept a pairing response when the negotiated mode is:
	/// <see cref="PairModes.ProvidePin"/> PIN is provided remotely.
	/// </summary>
	/// <param name="pin"> Set from user input </param>
	public class PinPairResponse(string pin) : IPairResponse
	{
		public string Pin { get; } = pin;
	}


	/// <summary>
	/// Use to Accept a pairing response when the negotiated mode is:
	/// <see cref="PairModes.ProvidePasswordCredential"/>
	/// </summary>
	/// <param name="userName"> The username of the credential. This value must not be null or empty. </param>
	/// <param name="password"> The password string of the credential. This value must not be null or empty</param>
	/// <param name="resource"></param>
	public class CredentialsPairResponse(string userName, string password, string resource = null) : IPairResponse
	{
		public string Password { get; } = password;
		public string UserName { get; } = userName;
		public string Resource { get; } = resource;
	}


	public interface IPairResponse;
}


/// <summary>
/// Suggested pairing options when requesting a Bond
/// </summary>
/// <param name="requestedModes"> Any or all modes acceptable for the application </param>
/// <param name="minimumRequestedProtection"> The minimal protection level (Authentication and or Encryption) that satisfies the application's communication requirements </param>
public class BondingOptions(PairModes requestedModes = PairModes.None, ProtectionLevel minimumRequestedProtection = ProtectionLevel.Unused)
{
	public PairModes RequestedModes { get; } = requestedModes;
	public ProtectionLevel MinimumRequestedProtection { get; } = minimumRequestedProtection;
}


/// <summary>
/// The final results of the Bond Request
/// </summary>
/// <param name="status"> Result status. This depends on platforms cooperation in reporting.
/// In addition to success, Windows and Android to a lesser extent may report information on failure </param>
/// <param name="detail"> String representation of the <see cref="BondResult.Status" </param>
/// <param name="protectionUsed"> The negotiated protection level (Authentication and or Encryption) used for communication with the remote device </param>
public class BondResult(DeviceBondStatus status, string detail = "", ProtectionLevel protectionUsed = ProtectionLevel.Unused)
{
	public DeviceBondStatus Status { get; } = status;
	public string Detail { get; } = detail;
	public ProtectionLevel ProtectionUsed { get; } = protectionUsed;
}


/// <summary>
/// For brevity, consolidates various unclear results of the Bonding process into a select few categories
/// </summary>
public enum DeviceBondStatus
{
	/// <summary>
	/// Result is simply that the device is known to be paired upon task completion. There may or may not be more information in <see cref="BondResult.Detail"/>
	/// </summary>
	Paired,

	/// <summary>
	/// The device is known not to be paired upon task completion. But there is ambiguity if the operation failed or was canceled/timed out, or otherwise; and the reasoning is not specified.
	/// 
	/// Android: Call to BondAsync does not start as OS rejects the request and the reason is Unspecified. No further support as the SDK fails to give any specific info on failures.
	/// Windows: Unknown failure to Bond.
	/// </summary>
	UnspecifiedFailure,

	/// <summary>
	/// The device is known not to be paired upon task completion. There is no ambiguity the operation failed, it wasn't canceled, timed out, or otherwise, and the reasoning is given.
	/// Such as the case when bonding is already in progress.  Check <see cref="BondResult.Detail"/> for the Specific failure.
	/// 
	/// Android: No further support as the SDK fails to give any other Specific info failures.
	/// Windows: For brevity, consolidates various Specific failures into a single Xplatform value.
	/// </summary>
	SpecifiedFailure
}


[Flags]
public enum PairModes
{
	/// <summary> No pairing is supported. </summary>
	None = 0,

	/// <summary>
	/// The application must confirm that it wishes to perform the pairing action. An optional confirmation dialog can be presented to the UI.
	/// The application must respond via <see cref="IPairProcess.PairRespondedEventArgs.ApprovePairingResponse"/> with <see cref="IPairProcess.ConfirmPairResponse"/> if the pairing is to complete.
	/// </summary>
	Consent = 0b1,

	/// <summary>
	/// It is intended for the application to display the given PIN so the user can enter it on the other device.
	/// The application must respond via <see cref="IPairProcess.PairRespondedEventArgs.ApprovePairingResponse"/> with <see cref="IPairProcess.ConfirmPairResponse"/> if the pairing is to complete.
	/// </summary>
	DisplayPin = 0b10,

	/// <summary>
	/// It is intended that the application request, either a known or remotely displayed PIN from the user.
	/// The application must respond via <see cref="IPairProcess.PairRespondedEventArgs.ApprovePairingResponse"/> passing the <see cref="IPairProcess.PinPairResponse.Pin"/> via <see cref="IPairProcess.PinPairResponse"/> if the pairing is to complete.
	/// </summary>
	ProvidePin = 0b100,

	/// <summary>
	/// It is intended that the application displays and allows the user to confirm the PIN matches on both devices.
	/// The application must respond via <see cref="IPairProcess.PairRespondedEventArgs.ApprovePairingResponse"/> with <see cref="IPairProcess.ConfirmPairResponse"/> if the pairing is to complete.
	/// </summary>
	ConfirmPinMatch = 0b1000,

	/// <summary>
	/// It is intended that the application request a username and password from the user.
	/// The application must respond via <see cref="IPairProcess.PairRespondedEventArgs.ApprovePairingResponse"/> with <see cref="IPairProcess.CredentialsPairResponse"/> if the pairing is to complete.
	/// </summary>
	ProvidePasswordCredential = 0B1_0000,

	/// <summary>
	/// Represents any of the available pairing methods.
	/// Intended for use when the application is prepared to accept any negotiated pairing mode
	/// </summary>
	Any = ProvidePasswordCredential | ConfirmPinMatch | ProvidePin | DisplayPin | Consent
}


public enum ProtectionLevel
{
	Unused,

	/// <summary>
	/// Pair the device using no level of protection.
	/// see: "Windows.Devices.Enumeration.DevicePairingProtectionLevel.None"
	/// </summary>
	None,

	/// <summary>
	/// Pair the device using encryption.
	/// see: "Windows.Devices.Enumeration.DevicePairingProtectionLevel.Encryption"
	/// </summary>
	Encryption,

	/// <summary>
	/// Pair the device using encryption and authentication.
	/// see: "Windows.Devices.Enumeration.DevicePairingProtectionLevel.EncryptionAndAuthentication"
	/// </summary>
	EncryptionAndAuthentication
}
