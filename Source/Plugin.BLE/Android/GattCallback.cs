using System;
using Android.Bluetooth;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.BLE.Android.CallbackEventArgs;

namespace Plugin.BLE.Android
{
    public interface IGattCallback
    {
        event EventHandler<ServicesDiscoveredCallbackEventArgs> ServicesDiscovered;
        event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueRead;
        event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueUpdated;
        event EventHandler<CharacteristicWriteCallbackEventArgs> CharacteristicValueWritten;
        event EventHandler<DescriptorCallbackEventArgs> DescriptorValueWritten;
        event EventHandler<DescriptorCallbackEventArgs> DescriptorValueRead;
        event EventHandler<RssiReadCallbackEventArgs> RemoteRssiRead;
        event EventHandler ConnectionInterrupted;
        event EventHandler<MtuRequestCallbackEventArgs> MtuRequested;
    }

    public class GattCallback : BluetoothGattCallback, IGattCallback
    {
        private readonly Adapter _adapter;
        private readonly Device _device;
        public event EventHandler<ServicesDiscoveredCallbackEventArgs> ServicesDiscovered;
        public event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueRead;
        public event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueUpdated;
        public event EventHandler<CharacteristicWriteCallbackEventArgs> CharacteristicValueWritten;
        public event EventHandler<RssiReadCallbackEventArgs> RemoteRssiRead;
        public event EventHandler ConnectionInterrupted;
        public event EventHandler<DescriptorCallbackEventArgs> DescriptorValueWritten;
        public event EventHandler<DescriptorCallbackEventArgs> DescriptorValueRead;
        public event EventHandler<MtuRequestCallbackEventArgs> MtuRequested;

        public GattCallback(Adapter adapter, Device device)
        {
            _adapter = adapter;
            _device = device;
        }

        public override void OnConnectionStateChange(BluetoothGatt gatt, GattStatus status, ProfileState newState)
        {
            base.OnConnectionStateChange(gatt, status, newState);

            if (!gatt.Device.Address.Equals(_device.NativeDevice.Address))
            {
                Trace.Message($"Gatt callback for device {_device.NativeDevice.Address} was called for device with address {gatt.Device.Address}. This shoud not happen. Please log an issue.");
                return;
            }

            //ToDo ignore just for me
            Trace.Message($"References of parent device and gatt callback device equal? {ReferenceEquals(_device.NativeDevice, gatt.Device).ToString().ToUpper()}");

            Trace.Message($"OnConnectionStateChange: GattStatus: {status}");

            switch (newState)
            {
                // disconnected
                case ProfileState.Disconnected:

                    // Close GATT if autoConnect is disabled, else we can accumulate zombie gatts.
                    if (!_device.ConnectParameters.AutoConnect)
                    {
                        CloseGattInstances(gatt);
                    }

                    // If status == 19, then connection was closed by the peripheral device (clean disconnect), consider this as a DeviceDisconnected
                    if (_device.IsOperationRequested || (int)status == 19)
                    {
                        Trace.Message("Disconnected by user");

                        //Found so we can remove it
                        _device.IsOperationRequested = false;
                        _adapter.ConnectedDeviceRegistry.TryRemove(gatt.Device.Address, out _);

                        if (status != GattStatus.Success && (int)status != 19)
                        {
                            // The above error event handles the case where the error happened during a Connect call, which will close out any waiting asyncs.
                            // Android > 5.0 uses this switch branch when an error occurs during connect
                            Trace.Message($"Error while connecting '{_device.Name}'. Not raising disconnect event.");
                            _adapter.HandleConnectionFail(_device, $"GattCallback error: {status}");
                        }
                        else
                        {
                            //we already hadled device error so no need th raise disconnect event(happens when device not in range)
                            _adapter.HandleDisconnectedDevice(true, _device);
                        }
                    }
                    else
                    {
                        //connection must have been lost, because the callback was not triggered by calling disconnect
                        Trace.Message($"Disconnected '{_device.Name}' by lost connection");

                        _adapter.ConnectedDeviceRegistry.TryRemove(gatt.Device.Address, out _);
                        _adapter.HandleDisconnectedDevice(false, _device);

                    }
                    // inform pending tasks
                    ConnectionInterrupted?.Invoke(this, EventArgs.Empty);
                    break;
                // connecting
                case ProfileState.Connecting:
                    Trace.Message("Connecting");
                    break;
                // connected
                case ProfileState.Connected:
                    Trace.Message("Connected");

                    //Check if the operation was requested by the user
                    if (_device.IsOperationRequested)
                    {
                        _device.Update(gatt.Device, gatt);

                        //Found so we can remove it
                        _device.IsOperationRequested = false;
                    }
                    else
                    {
                        //ToDo explore this
                        //only for on auto-reconnect (device is not in operation registry)
                        _device.Update(gatt.Device, gatt);
                    }

                    if (status != GattStatus.Success)
                    {
                        // The above error event handles the case where the error happened during a Connect call, which will close out any waiting asyncs.
                        // Android <= 4.4 uses this switch branch when an error occurs during connect
                        Trace.Message($"Error while connecting '{_device.Name}'. GattStatus: {status}. ");
                        _adapter.HandleConnectionFail(_device, $"GattCallback error: {status}");

                        CloseGattInstances(gatt);
                    }
                    else
                    {
                        _adapter.ConnectedDeviceRegistry[gatt.Device.Address] = _device;
                        _adapter.HandleConnectedDevice(_device);
                    }

                    break;
                // disconnecting
                case ProfileState.Disconnecting:
                    Trace.Message("Disconnecting");
                    break;
            }
        }

        private void CloseGattInstances(BluetoothGatt gatt)
        {
            //ToDO just for me
            Trace.Message($"References of parent device gatt and callback gatt equal? {ReferenceEquals(_device.Gatt, gatt).ToString().ToUpper()}");

            if (!ReferenceEquals(gatt, _device.Gatt))
            {
                gatt.Close();
            }

            //cleanup everything else
            _device.CloseGatt();
        }

        public override void OnServicesDiscovered(BluetoothGatt gatt, GattStatus status)
        {
            base.OnServicesDiscovered(gatt, status);

            Trace.Message("OnServicesDiscovered: {0}", status.ToString());

            ServicesDiscovered?.Invoke(this, new ServicesDiscoveredCallbackEventArgs());
        }

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, GattStatus status)
        {
            base.OnCharacteristicRead(gatt, characteristic, status);

            Trace.Message("OnCharacteristicRead: value {0}; status {1}", characteristic.GetValue().ToHexString(), status);

            CharacteristicValueRead?.Invoke(this, new CharacteristicReadCallbackEventArgs(characteristic, status));
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic)
        {
            base.OnCharacteristicChanged(gatt, characteristic);

            Trace.Message("OnCharacteristicChanged: value {0}", characteristic.GetValue().ToHexString());

            CharacteristicValueUpdated?.Invoke(this, new CharacteristicReadCallbackEventArgs(characteristic, GattStatus.Success));
        }

        public override void OnCharacteristicWrite(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, GattStatus status)
        {
            base.OnCharacteristicWrite(gatt, characteristic, status);

            Trace.Message("OnCharacteristicWrite: value {0} status {1}", characteristic.GetValue().ToHexString(), status);

            CharacteristicValueWritten?.Invoke(this, new CharacteristicWriteCallbackEventArgs(characteristic, status, GetExceptionFromGattStatus(status)));
        }

        public override void OnReliableWriteCompleted(BluetoothGatt gatt, GattStatus status)
        {
            base.OnReliableWriteCompleted(gatt, status);

            Trace.Message("OnReliableWriteCompleted: {0}", status);
        }

        public override void OnMtuChanged(BluetoothGatt gatt, int mtu, GattStatus status)
        {
            base.OnMtuChanged(gatt, mtu, status);

            Trace.Message("OnMtuChanged to value: {0}", mtu);

            MtuRequested?.Invoke(this, new MtuRequestCallbackEventArgs(GetExceptionFromGattStatus(status), mtu));
        }

        public override void OnReadRemoteRssi(BluetoothGatt gatt, int rssi, GattStatus status)
        {
            base.OnReadRemoteRssi(gatt, rssi, status);

            Trace.Message("OnReadRemoteRssi: device {0} status {1} value {2}", gatt.Device.Name, status, rssi);

            RemoteRssiRead?.Invoke(this, new RssiReadCallbackEventArgs(GetExceptionFromGattStatus(status), rssi));
        }

        public override void OnDescriptorWrite(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, GattStatus status)
        {
            base.OnDescriptorWrite(gatt, descriptor, status);

            Trace.Message("OnDescriptorWrite: {0}", descriptor.GetValue()?.ToHexString());

            DescriptorValueWritten?.Invoke(this, new DescriptorCallbackEventArgs(descriptor, GetExceptionFromGattStatus(status)));
        }

        public override void OnDescriptorRead(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, GattStatus status)
        {
            base.OnDescriptorRead(gatt, descriptor, status);

            Trace.Message("OnDescriptorRead: {0}", descriptor.GetValue()?.ToHexString());

            DescriptorValueRead?.Invoke(this, new DescriptorCallbackEventArgs(descriptor, GetExceptionFromGattStatus(status)));
        }

        private Exception GetExceptionFromGattStatus(GattStatus status)
        {
            Exception exception = null;
            switch (status)
            {
                case GattStatus.Failure:
                case GattStatus.InsufficientAuthentication:
                case GattStatus.InsufficientEncryption:
                case GattStatus.InvalidAttributeLength:
                case GattStatus.InvalidOffset:
                case GattStatus.ReadNotPermitted:
                case GattStatus.RequestNotSupported:
                case GattStatus.WriteNotPermitted:
                    exception = new Exception($"GattStatus: {(int)status} - {status.ToString()}");
                    break;
                case GattStatus.Success:
                    break;
                // default path to handle errors that are non standard BLE ones, but device priprietary ones.
                default:
                    exception = new Exception($"GattStatus: {(int)status}");
                    break;

            }

            return exception;
        }
    }
}
