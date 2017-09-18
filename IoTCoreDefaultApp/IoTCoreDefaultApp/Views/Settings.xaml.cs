﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Foundation;
using Windows.Security.Credentials;
using Windows.Services.Cortana;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Devices.Radios;
using Windows.Devices.Bluetooth;
using System.Linq;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace IoTCoreDefaultApp
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Settings : Page
    {
        private LanguageManager languageManager;
        private NetworkPresenter networkPresenter = new NetworkPresenter();
        private bool Automatic = true;
        private string CurrentPassword = string.Empty;
        // Device watcher
        private DeviceWatcher deviceWatcher = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformation> handlerAdded = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerUpdated = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerRemoved = null;
        private TypedEventHandler<DeviceWatcher, Object> handlerEnumCompleted = null;
        private TypedEventHandler<DeviceWatcher, Object> handlerStopped = null;
        // Pairing controls and notifications
        private enum MessageType { YesNoMessage, OKMessage, InformationalMessage };
        Windows.Devices.Enumeration.DevicePairingRequestedEventArgs pairingRequestedHandlerArgs;
        Windows.Foundation.Deferral deferral;
        Windows.Devices.Bluetooth.Rfcomm.RfcommServiceProvider provider = null; // To be used for inbound
        private string bluetoothConfirmOnlyFormatString;
        private string bluetoothDisplayPinFormatString;
        private string bluetoothConfirmPinMatchFormatString;
        private Windows.UI.Xaml.Controls.Button inProgressPairButton;
        Windows.UI.Xaml.Controls.Primitives.FlyoutBase savedPairButtonFlyout;

        private bool needsCortanaConsent = false;
        private bool cortanaConsentRequestedFromSwitch = false;

        static public ObservableCollection<BluetoothDeviceInformationDisplay> bluetoothDeviceObservableCollection
        {
            get;
            private set;
        } = new ObservableCollection<BluetoothDeviceInformationDisplay>();

        public Settings()
        {
            this.InitializeComponent();

            PreferencesListView.IsSelected = true;
            
            this.NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Enabled;

            this.DataContext = LanguageManager.GetInstance();

            this.Loaded += async (sender, e) =>
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    SetupLanguages();
                    screensaverToggleSwitch.IsOn = Screensaver.IsScreensaverEnabled;
                });
            };

            Window.Current.Activated += Window_Activated;
        }

        private void SetupLanguages()
        {
            languageManager = LanguageManager.GetInstance();

            LanguageComboBox.ItemsSource = languageManager.LanguageDisplayNames;
            LanguageComboBox.SelectedItem = LanguageManager.GetCurrentLanguageDisplayName();

            InputLanguageComboBox.ItemsSource = languageManager.InputLanguageDisplayNames;
            InputLanguageComboBox.SelectedItem = LanguageManager.GetCurrentInputLanguageDisplayName();
        }

        private async Task SetupNetworkAsync()
        {
            SetupEthernet();
            await RecreateWifiNetworkListAsync();
        }

        private void SetupBluetooth()
        {
            bluetoothDeviceListView.ItemsSource = bluetoothDeviceObservableCollection;
            RegisterForInboundPairingRequests();
        }

        private void SetupCortana()
        {
            var isCortanaSupported = false;
            try
            {
                isCortanaSupported = CortanaSettings.IsSupported();
            }
            catch (UnauthorizedAccessException)
            {
                // This is indicitive of EmbeddedMode not being enabled (i.e.
                // running IotCoreDefaultApp on Desktop or Mobile without 
                // enabling EmbeddedMode) 
                //  https://developer.microsoft.com/en-us/windows/iot/docs/embeddedmode
            }
            cortanaConsentRequestedFromSwitch = false;

            // Only allow the Cortana settings to be enabled if Cortana is available on this device
            CortanaVoiceActivationSwitch.IsEnabled = isCortanaSupported;
            CortanaAboutMeButton.IsEnabled = isCortanaSupported;

            // If Cortana is supported on this device and the user has never granted voice consent,
            // then set a flag so that each time this page is activated we will poll for
            // Cortana's Global Consent Value and update the UI if needed.
            if (isCortanaSupported)
            {
                var cortanaSettings = CortanaSettings.GetDefault();
                needsCortanaConsent = !cortanaSettings.HasUserConsentToVoiceActivation;

                // If consent isn't needed, then update the voice activation switch to reflect its current system state.
                if (!needsCortanaConsent)
                {
                    CortanaVoiceActivationSwitch.IsOn = cortanaSettings.IsVoiceActivationEnabled;
                }
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // Resource loading has to happen on the UI thread
            bluetoothConfirmOnlyFormatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothConfirmOnlyFormat");
            bluetoothDisplayPinFormatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothDisplayPinFormat");
            bluetoothConfirmPinMatchFormatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothConfirmPinMatchFormat");
            // Handle inbound pairing requests
            App.InboundPairingRequested += App_InboundPairingRequested;

            object oToggleSwitch = this.FindName("BluetoothToggle");
            if (oToggleSwitch != null)
            {
                var watcherToggle = oToggleSwitch as ToggleSwitch;
                if (watcherToggle.IsOn)
                {
                    if (deviceWatcher == null || (DeviceWatcherStatus.Stopped == deviceWatcher.Status))
                    {
                        StartWatchingAndDisplayConfirmationMessage();
                    }
                }
            }
            
            //Direct Jumping to Specific ListView from Outside
            if (null == e || null == e.Parameter)
            {
                await SwitchToSelectedSettingsAsync("PreferencesListViewItem");
                PreferencesListView.IsSelected = true;
            }
            else
            {
                await SwitchToSelectedSettingsAsync(e.Parameter.ToString());
            }
            
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopWatcher();
        }

        private async void App_InboundPairingRequested(object sender, InboundPairingEventArgs inboundArgs)
        {
            // Ignore the inbound if pairing is already in progress
            if (inProgressPairButton == null)
            {
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    // Make sure the Bluetooth grid is showing
                    SwitchToSelectedSettingsAsync("BluetoothListViewItem");

                    // Restore the ceremonies we registered with
                    var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    Object supportedPairingKinds = localSettings.Values["supportedPairingKinds"];
                    int iSelectedCeremonies = (int)DevicePairingKinds.ConfirmOnly;
                    if (supportedPairingKinds != null)
                    {
                        iSelectedCeremonies = (int)supportedPairingKinds;
                    }
                    SetSelectedCeremonies(iSelectedCeremonies);

                    // Clear any previous devices
                    bluetoothDeviceObservableCollection.Clear();

                    // Add latest
                    BluetoothDeviceInformationDisplay deviceInfoDisp = new BluetoothDeviceInformationDisplay(inboundArgs.DeviceInfo);
                    bluetoothDeviceObservableCollection.Add(deviceInfoDisp);

                    // Display a message about the inbound request
                    string formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothInboundPairingRequestFormat");
                    string confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
                    DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
                });
            }
        }

        private void StartWatchingAndDisplayConfirmationMessage()
        {
            // Clear the current collection
            bluetoothDeviceObservableCollection.Clear();
            // Start the watcher
            StartWatcher();
            // Display a message
            string confirmationMessage = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothOn");
            DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
        }

        private void BackButton_Clicked(object sender, RoutedEventArgs e)
        {
            NavigationUtils.GoBack();
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox.SelectedItem == null)
            {
                return;
            }

            languageManager.UpdateLanguage(comboBox.SelectedItem as string);
        }

        private void InputLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox.SelectedItem == null)
            {
                return;
            }

            languageManager.UpdateInputLanguage(comboBox.SelectedItem as string);
        }

        private void SetupEthernet()
        {
            var ethernetProfile = NetworkPresenter.GetDirectConnectionName();

            if (ethernetProfile == null)
            {
                NoneFoundText.Visibility = Visibility.Visible;
                DirectConnectionStackPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                NoneFoundText.Visibility = Visibility.Collapsed;
                DirectConnectionStackPanel.Visibility = Visibility.Visible;
            }
        }

        private async Task RecreateWifiNetworkListAsync()
        {
            if (await networkPresenter.WifiIsAvailable())
            {
                WifiListView.IsEnabled = false;

                ObservableCollection<WiFiAvailableNetwork> networks;
                try
                {
                    networks = new ObservableCollection<WiFiAvailableNetwork>(await networkPresenter.GetAvailableNetworks());
                }
                catch (Exception e)
                {
                    Debug.WriteLine(String.Format("Error scanning: 0x{0:X}: {1}", e.HResult, e.Message));
                    NoWifiFoundText.Text = e.Message;
                    NoWifiFoundText.Visibility = Visibility.Visible;
                    WifiListView.IsEnabled = true;
                    return;
                }

                if (networks.Count > 0)
                {

                    var connectedNetwork = networkPresenter.GetCurrentWifiNetwork();
                    if (connectedNetwork != null)
                    {
                        networks.Remove(connectedNetwork);
                        networks.Insert(0, connectedNetwork);
                        WifiListView.ItemsSource = networks;
                        SwitchToItemState(connectedNetwork, WifiConnectedState, true);
                    }
                    else
                    {
                        WifiListView.ItemsSource = networks;
                    }


                    NoWifiFoundText.Visibility = Visibility.Collapsed;
                    WifiListView.Visibility = Visibility.Visible;
                    WifiListView.IsEnabled = true;
                    return;
                }
            }

            NoWifiFoundText.Visibility = Visibility.Visible;
            WifiListView.Visibility = Visibility.Collapsed;
        }

        private void WifiListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var connectedNetwork = networkPresenter.GetCurrentWifiNetwork();
            var item = e.ClickedItem;
            if (connectedNetwork == item)
            {
                SwitchToItemState(item, WifiConnectedMoreOptions, true);
            }
        }

        private void WifiListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listView = sender as ListView;
            foreach (var item in e.RemovedItems)
            {
                SwitchToItemState(item, WifiInitialState, true);
            }

            foreach (var item in e.AddedItems)
            {
                Automatic = true;
                var connectedNetwork = networkPresenter.GetCurrentWifiNetwork();

                if (connectedNetwork == item)
                {
                    SwitchToItemState(connectedNetwork, WifiConnectedMoreOptions, true);
                }
                else
                {
                    SwitchToItemState(item, WifiConnectState, true);
                }
            }
        }

        private async void ConnectButton_Clicked(object sender, RoutedEventArgs e)
        {
            try
            {
                WifiListView.IsEnabled = false;

                var button = sender as Button;
                var network = button.DataContext as WiFiAvailableNetwork;
                if (NetworkPresenter.IsNetworkOpen(network))
                {
                    await ConnectToWifiAsync(network, null, Window.Current.Dispatcher);
                }
                else
                {
                    SwitchToItemState(network, WifiPasswordState, false);
                }
            }
            finally
            {
                WifiListView.IsEnabled = true;
            }
        }

        private async Task ConnectToWifiAsync(WiFiAvailableNetwork network, PasswordCredential credential, CoreDispatcher dispatcher)
        {
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SwitchToItemState(network, WifiConnectingState, false);
            });

            var didConnect = credential == null ?
                networkPresenter.ConnectToNetwork(network, Automatic):
                networkPresenter.ConnectToNetworkWithPassword(network, Automatic, credential);
            DataTemplate nextState = (await didConnect) ? WifiConnectedState : WifiInitialState;

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var list = WifiListView.ItemsSource as ObservableCollection<WiFiAvailableNetwork>;
                var itemLocation = list.IndexOf(network);
                if (0 != itemLocation)
                {
                    list.Move(itemLocation, 0);
                }
                var item = SwitchToItemState(network, nextState, true);
                if (item != null)
                {
                    item.IsSelected = true;
                }
            });
        }

        private void DisconnectButton_Clicked(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var network = button.DataContext as WiFiAvailableNetwork;
            var connectedNetwork = networkPresenter.GetCurrentWifiNetwork();

            if (network == connectedNetwork)
            {
                networkPresenter.DisconnectNetwork(network);
                var item = SwitchToItemState(network, WifiInitialState, true);
                item.IsSelected = false;
            }
        }

        private async void NextButton_Clicked(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            PasswordCredential credential;

            if (string.IsNullOrEmpty(CurrentPassword))
            {
                credential = null;
            }
            else
            {
                credential = new PasswordCredential()
                {
                    Password = CurrentPassword
                };
            }

            var network = button.DataContext as WiFiAvailableNetwork;
            await ConnectToWifiAsync(network, credential, Window.Current.Dispatcher);
        }

        private void CancelButton_Clicked(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = SwitchToItemState(button.DataContext, WifiInitialState, false);
            item.IsSelected = false;
        }

        private ListViewItem SwitchToItemState(object dataContext, DataTemplate template, bool forceUpdate)
        {
            if (forceUpdate)
            {
                WifiListView.UpdateLayout();
            }
            var item = WifiListView.ContainerFromItem(dataContext) as ListViewItem;
            if (item != null)
            {
                item.ContentTemplate = template;
            }
            return item;
        }

        private void ConnectAutomaticallyCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var checkbox = sender as CheckBox;

            Automatic = checkbox.IsChecked ?? false;
        }

        private void WifiPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var passwordBox = sender as PasswordBox;
            CurrentPassword = passwordBox.Password;
        }

        private async void SettingsChoice_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as FrameworkElement;
            if (item == null)
            {
                return;
            }

            // Language, Network, or Bluetooth settings etc.
            await SwitchToSelectedSettingsAsync(item.Name);
        }
        
        /// <summary>
        /// Helps Hiding all other Grid Views except the selected Grid
        /// </summary>
        /// <param name="itemName"></param>
        private async Task SwitchToSelectedSettingsAsync(string itemName)
        {
            switch (itemName)
            {
                case "PreferencesListViewItem":
                    NetworkGrid.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (BasicPreferencesGridView.Visibility == Visibility.Collapsed)
                    {
                        BasicPreferencesGridView.Visibility = Visibility.Visible;
                        PreferencesListView.IsSelected = true;
                    }
                    break;
                case "NetworkListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (NetworkGrid.Visibility == Visibility.Collapsed)
                    {
                        NetworkGrid.Visibility = Visibility.Visible;
                        NetworkListView.IsSelected = true;
                        await SetupNetworkAsync();
                    }
                    break;
                case "BluetoothListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    NetworkGrid.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (BluetoothGrid.Visibility == Visibility.Collapsed)
                    {
                        SetupBluetooth();
                        BluetoothGrid.Visibility = Visibility.Visible;
                        BluetoothListView.IsSelected = true;
                        if (await IsBluetoothEnabledAsync())
                        {
                            BluetoothToggle.IsOn = true;
                        }
                        else
                        {
                            TurnOffBluetooth();
                        }
                    }
                    break;
                case "CortanaListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    NetworkGrid.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;

                    if (CortanaGrid.Visibility == Visibility.Collapsed)
                    {
                        SetupCortana();
                        CortanaGrid.Visibility = Visibility.Visible;
                        CortanaListView.IsSelected = true;
                    }
                    break;
                default:
                    break;

            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshButton.IsEnabled = false;
            await RecreateWifiNetworkListAsync();
            RefreshButton.IsEnabled = true;
        }

        /// <summary>
        /// Start the Device Watcher and set callbacks to handle devices appearing and disappearing
        /// </summary>
        private void StartWatcher()
        {
            //ProtocolSelectorInfo protocolSelectorInfo;
            string aqsFilter = @"System.Devices.Aep.ProtocolId:=""{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"" OR System.Devices.Aep.ProtocolId:=""{bb7bb05e-5972-42b5-94fc-76eaa7084d49}""";  //Bluetooth + BluetoothLE

            // Request the IsPaired property so we can display the paired status in the UI
            string[] requestedProperties = { "System.Devices.Aep.IsPaired" };

            //// Get the device selector chosen by the UI, then 'AND' it with the 'CanPair' property
            //protocolSelectorInfo = (ProtocolSelectorInfo)selectorComboBox.SelectedItem;
            //aqsFilter = protocolSelectorInfo.Selector + " AND System.Devices.Aep.CanPair:=System.StructuredQueryType.Boolean#True";

            deviceWatcher = DeviceInformation.CreateWatcher(
                aqsFilter,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint
                );

            // Hook up handlers for the watcher events before starting the watcher

            handlerAdded = new TypedEventHandler<DeviceWatcher, DeviceInformation>(async (watcher, deviceInfo) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    bluetoothDeviceObservableCollection.Add(new BluetoothDeviceInformationDisplay(deviceInfo));
                });
            });
            deviceWatcher.Added += handlerAdded;

            handlerUpdated = new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    // Find the corresponding updated DeviceInformation in the collection and pass the update object
                    // to the Update method of the existing DeviceInformation. This automatically updates the object
                    // for us.
                    foreach (BluetoothDeviceInformationDisplay deviceInfoDisp in bluetoothDeviceObservableCollection)
                    {
                        if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            deviceInfoDisp.Update(deviceInfoUpdate);
                            break;
                        }
                    }
                });
            });
            deviceWatcher.Updated += handlerUpdated;

            handlerRemoved = new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
            {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    // Find the corresponding DeviceInformation in the collection and remove it
                    foreach (BluetoothDeviceInformationDisplay deviceInfoDisp in bluetoothDeviceObservableCollection)
                    {
                        if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            bluetoothDeviceObservableCollection.Remove(deviceInfoDisp);
                            break;
                        }
                    }
                });
            });
            deviceWatcher.Removed += handlerRemoved;

            handlerEnumCompleted = new TypedEventHandler<DeviceWatcher, Object>(async (watcher, obj) =>
            {
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    // Finished enumerating
                });
            });
            deviceWatcher.EnumerationCompleted += handlerEnumCompleted;

            handlerStopped = new TypedEventHandler<DeviceWatcher, Object>(async (watcher, obj) =>
            {
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    // Device watcher stopped
                });
            });
            deviceWatcher.Stopped += handlerStopped;

            // Start the Device Watcher
            deviceWatcher.Start();
        }

        /// <summary>
        /// Stop the Device Watcher
        /// </summary>
        private void StopWatcher()
        {
            if (null != deviceWatcher)
            {
                // First unhook all event handlers except the stopped handler. This ensures our
                // event handlers don't get called after stop, as stop won't block for any "in flight" 
                // event handler calls.  We leave the stopped handler as it's guaranteed to only be called
                // once and we'll use it to know when the query is completely stopped. 
                deviceWatcher.Added -= handlerAdded;
                deviceWatcher.Updated -= handlerUpdated;
                deviceWatcher.Removed -= handlerRemoved;
                deviceWatcher.EnumerationCompleted -= handlerEnumCompleted;

                if (DeviceWatcherStatus.Started == deviceWatcher.Status ||
                    DeviceWatcherStatus.EnumerationCompleted == deviceWatcher.Status)
                {
                    deviceWatcher.Stop();
                }
            }
        }

        /// <summary>
        /// This is really just a replacement for MessageDialog, which you can't use on Athens
        /// </summary>
        /// <param name="confirmationMessage"></param>
        /// <param name="messageType"></param>
        private async void DisplayMessagePanelAsync(string confirmationMessage, MessageType messageType)
        {
            // Use UI thread
            await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {

                confirmationText.Text = confirmationMessage;
                if (messageType == MessageType.OKMessage)
                {
                    yesButton.Content = "OK";
                    yesButton.Visibility = Visibility.Visible;
                    noButton.Visibility = Visibility.Collapsed;
                }
                else if (messageType == MessageType.InformationalMessage)
                {
                    // Just make the buttons invisible
                    yesButton.Visibility = Visibility.Collapsed;
                    noButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    yesButton.Content = "Yes";
                    yesButton.Visibility = Visibility.Visible;
                    noButton.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>
        /// The Yes or OK button on the DisplayConfirmationPanelAndComplete - accepts the pairing, completes the deferral and clears the message panel
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            // Accept the pairing
            AcceptPairing();
            // Clear the confirmation message
            ClearConfirmationPanel();
        }

        private void CompleteDeferral()
        {
            // Complete the deferral
            if (deferral != null)
            {
                deferral.Complete();
                deferral = null;
            }
        }

        /// <summary>
        /// Accept the pairing and complete the deferral
        /// </summary>
        private void AcceptPairing()
        {
            if (pairingRequestedHandlerArgs != null)
            {
                pairingRequestedHandlerArgs.Accept();
                pairingRequestedHandlerArgs = null;
            }
            // Complete deferral
            CompleteDeferral();
        }

        private void ClearConfirmationPanel()
        {
            confirmationText.Text = "";
            yesButton.Visibility = Visibility.Collapsed;
            noButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// The No button on the DisplayConfirmationPanelAndComplete - completes the deferral and clears the message panel
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            //Complete the deferral
            CompleteDeferral();
            // Clear the confirmation message
            ClearConfirmationPanel();
        }

        /// <summary>
        /// User wants to use custom pairing with the selected ceremony types and Default protection level
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void PairButton_Click(object sender, RoutedEventArgs e)
        {
            // Use the pair button on the bluetoothDeviceListView.SelectedItem to get the data context
            BluetoothDeviceInformationDisplay deviceInfoDisp =
                ((Button)sender).DataContext as BluetoothDeviceInformationDisplay;
            string formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothAttemptingToPairFormat");
            string confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);

            // Save the pair button
            Button pairButton = sender as Button;
            inProgressPairButton = pairButton;

            // Save the flyout and set to null so it doesn't pop up unless we want it
            savedPairButtonFlyout = pairButton.Flyout;
            inProgressPairButton.Flyout = null;

            // Disable the pair button until we are done
            pairButton.IsEnabled = false;

            // Get ceremony type and protection level selections
            DevicePairingKinds ceremoniesSelected = GetSelectedCeremonies();
            // Get protection level
            DevicePairingProtectionLevel protectionLevel = DevicePairingProtectionLevel.Default;

            // Specify custom pairing with all ceremony types and protection level EncryptionAndAuthentication
            DeviceInformationCustomPairing customPairing = deviceInfoDisp.DeviceInformation.Pairing.Custom;

            customPairing.PairingRequested += PairingRequestedHandler;
            DevicePairingResult result = await customPairing.PairAsync(ceremoniesSelected, protectionLevel);

            if (result.Status == DevicePairingResultStatus.Paired)
            {
                formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothPairingSuccessFormat");
                confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            else
            {
                formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothPairingFailureFormat");
                confirmationMessage = string.Format(formatString, result.Status.ToString(), deviceInfoDisp.Name,
                    deviceInfoDisp.Id);
            }
            // Display the result of the pairing attempt
            DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);

            // If the watcher toggle is on, clear any devices in the list and stop and restart the watcher to ensure state is reflected in list
            if (BluetoothToggle.IsOn)
            {
                bluetoothDeviceObservableCollection.Clear();
                StopWatcher();
                StartWatcher();
            }
            else
            {
                // If the watcher is off this is an inbound request so just clear the list
                bluetoothDeviceObservableCollection.Clear();
            }

            // Re-enable the pair button
            inProgressPairButton = null;
            pairButton.IsEnabled = true;
        }

        /// <summary>
        /// Called when custom pairing is initiated so that we can handle the custom ceremony
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void PairingRequestedHandler(
            DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            // Save the args for use in ProvidePin case
            pairingRequestedHandlerArgs = args;

            // Save the deferral away and complete it where necessary.
            if (args.PairingKind != DevicePairingKinds.DisplayPin)
            {
                deferral = args.GetDeferral();
            }

            string confirmationMessage;

            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    // Windows itself will pop the confirmation dialog as part of "consent" if this is running on Desktop or Mobile
                    // If this is an App for Athens where there is no Windows Consent UX, you may want to provide your own confirmation.
                    {
                        confirmationMessage = string.Format(bluetoothConfirmOnlyFormatString, args.DeviceInformation.Name, args.DeviceInformation.Id);
                        DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
                        // Accept the pairing which also completes the deferral
                        AcceptPairing();
                    }
                    break;

                case DevicePairingKinds.DisplayPin:
                    // We just show the PIN on this side. The ceremony is actually completed when the user enters the PIN
                    // on the target device
                    {
                        confirmationMessage = string.Format(bluetoothDisplayPinFormatString, args.Pin);
                        DisplayMessagePanelAsync(confirmationMessage, MessageType.OKMessage);
                    }
                    break;

                case DevicePairingKinds.ProvidePin:
                    // A PIN may be shown on the target device and the user needs to enter the matching PIN on 
                    // this Windows device.
                    await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        // PIN Entry
                        inProgressPairButton.Flyout = savedPairButtonFlyout;
                        inProgressPairButton.Flyout.ShowAt(inProgressPairButton);
                    });
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                    // We show the PIN here and the user responds with whether the PIN matches what they see
                    // on the target device. Response comes back and we set it on the PinComparePairingRequestedData
                    // then complete the deferral.
                    {
                        confirmationMessage = string.Format(bluetoothConfirmPinMatchFormatString, args.Pin);
                        DisplayMessagePanelAsync(confirmationMessage, MessageType.YesNoMessage);
                    }
                    break;
            }
        }

        /// <summary>
        /// Turn on Bluetooth Radio and list available Bluetooth Devices
        /// </summary>
        private async void TurnOnRadio()
        {
            await ToggleBluetoothAsync(true);
            SetupBluetooth();
        }

        /// <summary>
        /// Checks the state of Bluetooth Radio 
        /// </summary>
        private async Task<bool> IsBluetoothEnabledAsync()
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            return bluetoothRadio != null && bluetoothRadio.State == RadioState.On;
        }

        private async Task ToggleBluetoothAsync(bool bluetoothState)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed)
                {
                    return;
                }
                BluetoothAdapter adapter = await BluetoothAdapter.GetDefaultAsync();
                if(null != adapter )
                {
                    var btRadio = await adapter.GetRadioAsync();
                    if (bluetoothState)
                    {
                        await btRadio.SetStateAsync(RadioState.On);
                    }
                    else
                    {
                        await btRadio.SetStateAsync(RadioState.Off);
                    }
                }
                
            }
            catch (Exception e)
            {
                string formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothNoDeviceAvailableFormat");
                string confirmationMessage = string.Format(formatString, e.Message);
                DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
            }
        }

        /// <summary>
        /// Turn off Bluetooth Radio and stops watching for Bluetooth devices
        /// </summary>
        private async void TurnOffBluetooth()
        {
            // Clear any devices in the list
            bluetoothDeviceObservableCollection.Clear();
            // Stop the watcher
            StopWatcher();
            // Display a message
            string confirmationMessage = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothOff");
            DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
            await ToggleBluetoothAsync(false);
        }

        /// <summary>
        /// User wants to unpair from the selected device
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void UnpairButton_Click(object sender, RoutedEventArgs e)
        {
            // Use the unpair button on the bluetoothDeviceListView.SelectedItem to get the data context
            BluetoothDeviceInformationDisplay deviceInfoDisp = ((Button)sender).DataContext as BluetoothDeviceInformationDisplay;
            string formatString;
            string confirmationMessage;

            Button unpairButton = sender as Button;
            // Disable the unpair button until we are done
            unpairButton.IsEnabled = false;

            DeviceUnpairingResult unpairingResult = await deviceInfoDisp.DeviceInformation.Pairing.UnpairAsync();

            if (unpairingResult.Status == DeviceUnpairingResultStatus.Unpaired)
            {
                // Device is unpaired
                formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothUnpairingSuccessFormat");
                confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            else
            {
                formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothUnpairingFailureFormat");
                confirmationMessage = string.Format(formatString, unpairingResult.Status.ToString(), deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            // Display the result of the pairing attempt
            DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);

            // If the watcher toggle is on, clear any devices in the list and stop and restart the watcher to ensure state is reflected in list
            if (BluetoothToggle.IsOn)
            {
                bluetoothDeviceObservableCollection.Clear();
                StopWatcher();
                StartWatcher();
            }
            else
            {
                // If the watcher is off this is an inbound request so just clear the list
                bluetoothDeviceObservableCollection.Clear();
            }

            // Re-enable the unpair button
            unpairButton.IsEnabled = true;
        }

        /// <summary>
        /// User has entered a PIN and pressed <Return> in the PIN entry flyout
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PinEntryTextBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                //  Close the flyout and save the PIN the user entered
                TextBox bluetoothPINTextBox = sender as TextBox;
                string pairingPIN = bluetoothPINTextBox.Text;
                if (pairingPIN != "")
                {
                    // Hide the flyout
                    inProgressPairButton.Flyout.Hide();
                    inProgressPairButton.Flyout = null;
                    // Use the PIN to accept the pairing
                    AcceptPairingWithPIN(pairingPIN);
                }
            }
        }

        private void AcceptPairingWithPIN(string PIN)
        {
            if (pairingRequestedHandlerArgs != null)
            {
                pairingRequestedHandlerArgs.Accept(PIN);
                pairingRequestedHandlerArgs = null;
            }
            // Complete the deferral here
            CompleteDeferral();
        }

        /// <summary>
        /// Call when selection changes on the list of discovered Bluetooth devices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        /// <summary>
        /// Get the set of acceptable ceremonies from the check boxes
        /// </summary>
        /// <returns></returns>
        private DevicePairingKinds GetSelectedCeremonies()
        {
            DevicePairingKinds ceremonySelection = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
            return ceremonySelection;
        }

        /// <summary>
        /// Set the check boxes to refelect the set of acceptable ceremonies
        /// </summary>
        /// <param name="selectedCeremonies"></param>
        private void SetSelectedCeremonies(int selectedCeremonies)
        {
            // Currently a no-op, but would be used if checkboxes are added to restrict ceremony types
        }

        private async void RegisterForInboundPairingRequests()
        {
            // Make the system discoverable for Bluetooth
            await MakeDiscoverable();

            // If the attempt to make the system discoverable failed then likely there is no Bluetooth device present
            // so leave the diagnositic message put out by the call to MakeDiscoverable()
            if (App.IsBluetoothDiscoverable)
            {
                string formatString;
                string confirmationMessage;

                // Get state of ceremony checkboxes
                DevicePairingKinds ceremoniesSelected = GetSelectedCeremonies();
                int iCurrentSelectedCeremonies = (int)ceremoniesSelected;

                // Find out if we changed the ceremonies we orginally registered with - if we have registered before these will be saved
                var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
                Object supportedPairingKinds = localSettings.Values["supportedPairingKinds"];
                int iSavedSelectedCeremonies = -1; // Deliberate impossible value
                if (supportedPairingKinds != null)
                {
                    iSavedSelectedCeremonies = (int)supportedPairingKinds;
                }

                if (!DeviceInformationPairing.TryRegisterForAllInboundPairingRequests(ceremoniesSelected))
                {
                    confirmationMessage = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothInboundRegistrationFailed");
                }
                else
                {
                    // Save off the ceremonies we registered with
                    localSettings.Values["supportedPairingKinds"] = iCurrentSelectedCeremonies;
                    formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothInboundRegistrationSucceededFormat");
                    confirmationMessage = string.Format(formatString, ceremoniesSelected.ToString());
                }

                // Clear the current collection
                bluetoothDeviceObservableCollection.Clear();
                // Start the watcher
                StartWatcher();
                // Display a message
                confirmationMessage += BluetoothDeviceInformationDisplay.GetResourceString("BluetoothOn");
                DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
            }
        }

        private async System.Threading.Tasks.Task MakeDiscoverable()
        {
            // Make the system discoverable. Don'd repeatedly do this or the StartAdvertising will throw "cannot create a file when that file already exists"
            if (!App.IsBluetoothDiscoverable)
            {
                Guid BluetoothServiceUuid = new Guid("17890000-0068-0069-1532-1992D79BE4D8");
                try
                {
                    provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(BluetoothServiceUuid));
                    Windows.Networking.Sockets.StreamSocketListener listener = new Windows.Networking.Sockets.StreamSocketListener();
                    listener.ConnectionReceived += OnConnectionReceived;
                    await listener.BindServiceNameAsync(provider.ServiceId.AsString(), Windows.Networking.Sockets.SocketProtectionLevel.PlainSocket);
                    //     SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                    // Don't bother setting SPD attributes
                    provider.StartAdvertising(listener, true);
                    App.IsBluetoothDiscoverable = true;
                }
                catch (Exception e)
                {
                    string formatString = BluetoothDeviceInformationDisplay.GetResourceString("BluetoothNoDeviceAvailableFormat");
                    string confirmationMessage = string.Format(formatString, e.Message);
                    DisplayMessagePanelAsync(confirmationMessage, MessageType.InformationalMessage);
                }
            }
        }

        /// <summary>
        /// We have to have a callback handler to handle "ConnectionReceived" but we don't do anything because
        /// the StartAdvertising is just a way to turn on Bluetooth discoverability
        /// </summary>
        /// <param name="listener"></param>
        /// <param name="args"></param>
        void OnConnectionReceived(Windows.Networking.Sockets.StreamSocketListener listener,
                                   Windows.Networking.Sockets.StreamSocketListenerConnectionReceivedEventArgs args)
        {
        }

        private void BluetoothToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var bluetoothOnOffSwitch = sender as ToggleSwitch;
            if (bluetoothOnOffSwitch.IsOn)
            {
                TurnOnRadio();
            }
            else
            {
                TurnOffBluetooth();
            }
        }

        private void Screensaver_Toggled(object sender, RoutedEventArgs e)
        {
            var screensaverToggleSwitch = sender as ToggleSwitch;
            Screensaver.IsScreensaverEnabled = screensaverToggleSwitch.IsOn;
        }

        private void WifiPasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            var passwordBox = sender as PasswordBox;
            if (passwordBox != null)
            {
                passwordBox.Focus(FocusState.Programmatic);
            }
        }

        private void CortanaVoiceActivationSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            var cortanaSettings = CortanaSettings.GetDefault();
            var cortanaVoiceActivationSwitch = (ToggleSwitch)sender;

            bool enableVoiceActivation = cortanaVoiceActivationSwitch.IsOn;

            // If user is requesting to turn on voice activation, but consent has not been provided yet, then launch Cortana to ask for consent first
            if (!cortanaSettings.HasUserConsentToVoiceActivation)
            {
                // Guard against the case where the switch is toggled off when Consent hasn't been given yet
                // This occurs when we are re-entering this method when the switch is turned off in the code that follows
                if (!enableVoiceActivation)
                {
                    return;
                }

                // Launch Cortana to get the User Consent.  This is required before a change to enable voice activation is permitted
                CortanaVoiceActivationSwitch.IsEnabled = false;
                needsCortanaConsent = true;
                CortanaVoiceActivationSwitch.IsOn = false;
                cortanaConsentRequestedFromSwitch = true;
                CortanaHelper.LaunchCortanaToConsentPageAsync();
            }
            // Otherwise, we already have consent, so just enable or disable the voice activation setting.
            // Do this asynchronously because the API waits for the SpeechRuntime EXE to launch
            else
            {
                CortanaVoiceActivationSwitch.IsEnabled = false;
                Window.Current.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await SetVoiceActivation(enableVoiceActivation);
                    CortanaVoiceActivationSwitch.IsEnabled = true;
                });
            }
        }

        private async void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            switch (e.WindowActivationState)
            {
                case CoreWindowActivationState.PointerActivated:
                case CoreWindowActivationState.CodeActivated:
                    if (needsCortanaConsent)
                    {
                        // Re-enable the voice activation selection
                        CortanaVoiceActivationSwitch.IsEnabled = true;

                        // Verify whether consent has changed while the screen was away
                        var cortanaSettings = CortanaSettings.GetDefault();
                        if (cortanaSettings.HasUserConsentToVoiceActivation)
                        {
                            // Consent was granted, so finish the task of flipping the switch to the current activation-state
                            // (It is possible that Cortana Consent was granted by some other application, while
                            // the default app was running, but not by the user actively flipping the switch,
                            // so update the switch state to the current global setting)                           
                            if (cortanaConsentRequestedFromSwitch)
                            {
                                await SetVoiceActivation(true);
                                cortanaConsentRequestedFromSwitch = false;
                            }

                            // Set the switch to the current global state
                            CortanaVoiceActivationSwitch.IsOn = cortanaSettings.IsVoiceActivationEnabled;

                            // We no longer need consent
                            needsCortanaConsent = false;
                        }
                    }
                    break;

                default:
                    break;
            }
        }

        const int RPC_S_CALL_FAILED = -2147023170;
        const int RPC_S_SERVER_UNAVAILABLE = -2147023174;
        const int RPC_S_SERVER_TOO_BUSY = -2147023175;
        const int MAX_VOICEACTIVATION_TRIALS = 5;
        const int TIMEINTERVAL_VOICEACTIVATION = 10;    // milli sec
        private async Task SetVoiceActivation(bool value)
        {
            var cortanaSettings = CortanaSettings.GetDefault();
            for (int i = 0; i < MAX_VOICEACTIVATION_TRIALS; i++)
            {
                try
                {
                    cortanaSettings.IsVoiceActivationEnabled = value;
                }
                catch (System.Exception ex)
                {
                    if (ex.HResult == RPC_S_CALL_FAILED ||
                        ex.HResult == RPC_S_SERVER_UNAVAILABLE ||
                        ex.HResult == RPC_S_SERVER_TOO_BUSY)
                    {
                        // VoiceActivation server is very likely busy =>
                        // yield and take a new ref to CortanaSettings API
                        await Task.Delay(TimeSpan.FromMilliseconds(TIMEINTERVAL_VOICEACTIVATION));
                        cortanaSettings = CortanaSettings.GetDefault();
                    }
                    else
                    {
                        throw ex;
                    }
                }
            }
        }

        private async void CortanaAboutMeButton_Click(object sender, RoutedEventArgs e)
        {
            await CortanaHelper.LaunchCortanaToAboutMeAsync();
        }
        
    }
}