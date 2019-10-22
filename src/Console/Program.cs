﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Console.Commands;
using Console.Configuration;
using log4net;
using log4net.Config;
using OSDP.Net;
using OSDP.Net.Connections;
using OSDP.Net.Messages;
using Terminal.Gui;

namespace Console
{
    internal static class Program
    {
        private static readonly ControlPanel ControlPanel = new ControlPanel();
        private static readonly Queue<string> Messages = new Queue<string>();
        private static readonly object MessageLock = new object();

        private static readonly MenuBarItem DevicesMenuBarItem =
            new MenuBarItem("_Devices", new[]
            {
                new MenuItem("_Add", string.Empty, AddDevice),
                // new MenuItem("_List", "", AddDevice),
                // new MenuItem("_Send Command", "", AddDevice),
                new MenuItem("_Remove", string.Empty, RemoveDevice)
            });

        private static Guid _connectionId;
        private static Window _window;
        private static MenuBar _menuBar;
        private static ControlPanel.NakReplyEventArgs _lastNak;

        private static Settings _settings;

        private static void Main()
        {
            XmlConfigurator.Configure(
                LogManager.GetRepository(Assembly.GetAssembly(typeof(LogManager))),
                new FileInfo("log4net.config"));

            _settings = GetConnectionSettings();

            Application.Init();

            _window = new Window("OSDP.Net")
            {
                X = 0,
                Y = 1, // Leave one row for the toplevel menu

                Width = Dim.Fill(),
                Height = Dim.Fill() - 1
            };

            _menuBar = new MenuBar(new[]
            {
                new MenuBarItem("_System", new []
                {
                    new MenuItem("Start _Serial Connection", "", StartSerialConnection),
                    new MenuItem("Start _TCP Server Connection", "", StartTcpServerConnection),
                    new MenuItem("Sto_p Connections", "", ControlPanel.Shutdown),
                    new MenuItem("Show _Log", string.Empty, ShowLog),
                    new MenuItem("Save _Configuration", "", () => SetConnectionSettings(_settings)),
                    new MenuItem("_Quit", "", () =>
                    {
                        if (MessageBox.Query(40, 10, "Quit", "Quit program?", "Yes", "No") == 0)
                        {
                            Application.RequestStop();
                        }
                    })
                }),
                DevicesMenuBarItem,
                new MenuBarItem("_Commands", new[]
                {
                    new MenuItem("_Device Capabilities", "", 
                        () => SendCommand("Device capabilities", _connectionId, ControlPanel.DeviceCapabilities)),
                    new MenuItem("_ID Report", "", 
                        () => SendCommand("ID report", _connectionId, ControlPanel.IdReport)),
                    new MenuItem("Input Status", "", 
                        () => SendCommand("Input status", _connectionId, ControlPanel.InputStatus)),
                    new MenuItem("_Local Status", "", 
                        () => SendCommand("Local status", _connectionId, ControlPanel.LocalStatus)),
                    new MenuItem("Output Status", "", 
                        () => SendCommand("Output status", _connectionId, ControlPanel.OutputStatus))
                }),
                new MenuBarItem("_Invalid Commands", new[]
                {
                    new MenuItem("_Bad CRC/Checksum", "", 
                        () => SendCustomCommand("Bad CRC/Checksum", _connectionId, ControlPanel.SendCustomCommand,
                            address => new InvalidCrcPollCommand(address)))
                })
            });

            Application.Top.Add(_menuBar, _window);

            Application.Run();

            ControlPanel.Shutdown();
        }

        private static void ShowLog()
        {
            _window.RemoveAll();
            var scrollView = new ScrollView(new Rect(1, 0, _window.Frame.Width - 1, _window.Frame.Height - 1))
            {
                ContentSize = new Size(100, 100),
                ShowVerticalScrollIndicator = true,
                ShowHorizontalScrollIndicator = true
            };
            lock (MessageLock)
            {
                scrollView.Add(new Label(0, 0, string.Join("", Messages.Reverse().ToArray())));
            }

            _window.Add(scrollView);
        }

        private static void StartSerialConnection()
        {
            var portNameTextField = new TextField(15, 1, 35, _settings.SerialConnectionSettings.PortName);
            var baudRateTextField = new TextField(15, 3, 35, _settings.SerialConnectionSettings.BaudRate.ToString());

            void StartConnectionButtonClicked()
            {
                _settings.SerialConnectionSettings.PortName = portNameTextField.Text.ToString();
                if (!int.TryParse(baudRateTextField.Text.ToString(), out var baudRate))
                {

                    MessageBox.ErrorQuery(40, 10, "Error", "Invalid baud rate entered!", "OK");
                    return;
                }

                _settings.SerialConnectionSettings.BaudRate = baudRate;
                
                StartConnection(new SerialPortOsdpConnection(_settings.SerialConnectionSettings.PortName,
                    _settings.SerialConnectionSettings.BaudRate));
                
                Application.RequestStop();
            }
            
            Application.Run(new Dialog("Start Serial Connection", 60, 10,
                new Button("Start") {Clicked = StartConnectionButtonClicked},
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                new Label(1, 1, "Port:"),
                portNameTextField,
                new Label(1, 3, "Baud Rate:"),
                baudRateTextField
            });
        }

        private static void StartTcpServerConnection()
        {
            var portNumberTextField = new TextField(15, 1, 35, _settings.TcpServerConnectionSettings.PortNumber.ToString());
            var baudRateTextField = new TextField(15, 3, 35, _settings.SerialConnectionSettings.BaudRate.ToString());

            void StartConnectionButtonClicked()
            {
                if (!int.TryParse(portNumberTextField.Text.ToString(), out var portNumber))
                {

                    MessageBox.ErrorQuery(40, 10, "Error", "Invalid port number entered!", "OK");
                    return;
                }
                _settings.TcpServerConnectionSettings.BaudRate = portNumber;
                
                if (!int.TryParse(baudRateTextField.Text.ToString(), out var baudRate))
                {

                    MessageBox.ErrorQuery(40, 10, "Error", "Invalid baud rate entered!", "OK");
                    return;
                }
                _settings.TcpServerConnectionSettings.BaudRate = baudRate;
                
                StartConnection( new TcpServerOsdpConnection(_settings.TcpServerConnectionSettings.BaudRate = portNumber,
                    _settings.TcpServerConnectionSettings.BaudRate));
                
                Application.RequestStop();
            }
            
            Application.Run(new Dialog("Start TCP Server Connection", 60, 10,
                new Button("Start") {Clicked = StartConnectionButtonClicked},
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                new Label(1, 1, "Port Number:"),
                portNumberTextField,
                new Label(1, 3, "Baud Rate:"),
                baudRateTextField
            });
        }

        private static void StartConnection(IOsdpConnection osdpConnection)
        {
            ControlPanel.Shutdown();

            _connectionId = ControlPanel.StartConnection(osdpConnection);
            
            foreach (var device in _settings.Devices)
            {
                ControlPanel.AddDevice(_connectionId, device.Address, device.UseCrc, device.UseSecureChannel);
            }

            ControlPanel.NakReplyReceived += (sender, args) =>
            {
                var lastNak = _lastNak;
                _lastNak = args;
                if (lastNak != null && lastNak.Address == args.Address &&
                    lastNak.Nak.ErrorCode == args.Nak.ErrorCode)
                {
                    return;
                }

                Application.MainLoop.Invoke(() =>
                    DisplayMessage($"!!! Received NAK reply for address {args.Address} !!!",
                        args.Nak.ToString()));
            };
            ControlPanel.LocalStatusReportReplyReceived += (sender, args) =>
            {
                DisplayReceivedReply($"Local status updated for address {args.Address}",
                    args.LocalStatus.ToString());
            };
            ControlPanel.InputStatusReportReplyReceived += (sender, args) =>
            {
                DisplayReceivedReply($"Input status updated for address {args.Address}",
                    args.InputStatus.ToString());
            };
            ControlPanel.OutputStatusReportReplyReceived += (sender, args) =>
            {
                DisplayReceivedReply($"Output status updated for address {args.Address}",
                    args.OutputStatus.ToString());
            };
            ControlPanel.RawCardDataReplyReceived += (sender, args) =>
            {
                DisplayReceivedReply($"Received raw card data reply for address {args.Address}",
                    args.RawCardData.ToString());
            };
        }

        private static void DisplayReceivedReply(string title, string message)
        {
            Application.MainLoop.Invoke(() =>DisplayMessage(title, message));
        }

        public static void AddLogMessage(string message)
        {
            lock (MessageLock)
            {
                Messages.Enqueue(message);
                while (Messages.Count > 100)
                {
                    Messages.Dequeue();
                }
            }
        }

        private static Settings GetConnectionSettings()
        {
            try
            {
                string json = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.config"));
                return JsonSerializer.Deserialize<Settings>(json);
            }
            catch
            {
                return new Settings();
            }
        }

        private static void SetConnectionSettings(Settings connectionSettings)
        {
            try
            {
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.config"),
                    JsonSerializer.Serialize(connectionSettings));
            }
            catch
            {
                // ignored
            }
        }

        private static void AddDevice()
        {
            var nameTextField = new TextField(15, 1, 35, string.Empty);
            var addressTextField = new TextField(15, 3, 35, string.Empty);
            var useCrcCheckBox = new CheckBox(1, 5, "Use CRC", true);
            var useSecureChannelCheckBox = new CheckBox(1, 6, "Use Secure Channel", true);

            void AddDeviceButtonClicked()
            {
                if (!byte.TryParse(addressTextField.Text.ToString(), out var address))
                {

                    MessageBox.ErrorQuery(40, 10, "Error", "Invalid address entered!", "OK");
                    return;
                }

                if (_settings.Devices.Any(device => device.Address == address))
                {
                    if (MessageBox.Query(60, 10, "Overwrite", "Device already exists at that address, overwrite?",
                            "Yes", "No") == 1)
                    {
                        return;
                    }
                }

                ControlPanel.AddDevice(_connectionId, address, useCrcCheckBox.Checked,
                    useSecureChannelCheckBox.Checked);

                var foundDevice = _settings.Devices.FirstOrDefault(device => device.Address == address);
                if (foundDevice != null)
                {
                    _settings.Devices.Remove(foundDevice);
                }

                _settings.Devices.Add(new DeviceSetting
                {
                    Address = address, Name = nameTextField.Text.ToString(),
                    UseSecureChannel = useSecureChannelCheckBox.Checked,
                    UseCrc = useCrcCheckBox.Checked
                });
                Application.RequestStop();
            }

            Application.Run(new Dialog("Add Device", 60, 13,
                new Button("Add") {Clicked = AddDeviceButtonClicked},
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                new Label(1, 1, "Name:"),
                nameTextField,
                new Label(1, 3, "Address:"),
                addressTextField,
                useCrcCheckBox,
                useSecureChannelCheckBox
            });
        }

        private static void RemoveDevice()
        {
            var orderedDevices = _settings.Devices.OrderBy(device => device.Address).ToArray();
            var scrollView = new ScrollView(new Rect(6, 1, 40, 6))
            {
                ContentSize = new Size(50, orderedDevices.Length * 2),
                ShowVerticalScrollIndicator = orderedDevices.Length > 6,
                ShowHorizontalScrollIndicator = false
            };

            var deviceRadioGroup = new RadioGroup(0, 0,
                orderedDevices.Select(device => $"{device.Address} : {device.Name}").ToArray());
            scrollView.Add(deviceRadioGroup);
            
            void RemoveDeviceButtonClicked()
            {
                var removedDevice = orderedDevices[deviceRadioGroup.Selected];
                ControlPanel.RemoveDevice(_connectionId, removedDevice.Address);
                _settings.Devices.Remove(removedDevice);
                Application.RequestStop();
            }

            Application.Run(new Dialog("Remove Device", 60, 13,
                new Button("Remove") {Clicked = RemoveDeviceButtonClicked},
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                scrollView
            });
        }

        private static void SendCommand<T>(string title, Guid connectionId, Func<Guid, byte, Task<T>> sendCommandFunction)
        {
            var orderedDevices = _settings.Devices.OrderBy(device => device.Address).ToArray();
            var scrollView = new ScrollView(new Rect(6, 1, 40, 6))
            {
                ContentSize = new Size(50, orderedDevices.Length * 2),
                ShowVerticalScrollIndicator = orderedDevices.Length > 6,
                ShowHorizontalScrollIndicator = false
            };

            var deviceRadioGroup = new RadioGroup(0, 0,
                orderedDevices.Select(device => $"{device.Address} : {device.Name}").ToArray());
            scrollView.Add(deviceRadioGroup);

            void SendCommandButtonClicked()
            {
                var selectedDevice = orderedDevices[deviceRadioGroup.Selected];
                byte address = selectedDevice.Address;
                Application.RequestStop();

                Task.Run(async () =>
                {
                    try
                    {
                        var result = await sendCommandFunction(connectionId, address);
                        Application.MainLoop.Invoke(() =>
                        {
                            DisplayMessage($"{title} for address {address}", result.ToString());
                        });
                    }
                    catch (Exception exception)
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            MessageBox.ErrorQuery(40, 10, $"Error on address {address}", exception.Message,
                                "OK");
                        });
                    }
                });
            }

            Application.Run(new Dialog(title, 60, 13,
                new Button("Send") {Clicked = SendCommandButtonClicked
                },
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                scrollView
            });
        }

        private static void SendCustomCommand(string title, Guid connectionId, Func<Guid, Command, Task> sendCommandFunction, Func<byte, Command> createCommand)
        {
            var orderedDevices = _settings.Devices.OrderBy(device => device.Address).ToArray();
            var scrollView = new ScrollView(new Rect(6, 1, 40, 6))
            {
                ContentSize = new Size(50, orderedDevices.Length * 2),
                ShowVerticalScrollIndicator = orderedDevices.Length > 6,
                ShowHorizontalScrollIndicator = false
            };

            var deviceRadioGroup = new RadioGroup(0, 0,
                orderedDevices.Select(device => $"{device.Address} : {device.Name}").ToArray());
            scrollView.Add(deviceRadioGroup);

            void SendCommandButtonClicked()
            {
                var selectedDevice = orderedDevices[deviceRadioGroup.Selected];
                byte address = selectedDevice.Address;
                Application.RequestStop();
                
                Task.Run(async () =>
                {
                    try
                    {
                        await sendCommandFunction(connectionId, createCommand(address));
                    }
                    catch (Exception exception)
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            MessageBox.ErrorQuery(40, 10, $"Error on address {address}", exception.Message,
                                "OK");
                        });
                    }
                });
            }

            Application.Run(new Dialog(title, 60, 13,
                new Button("Send") {Clicked = SendCommandButtonClicked
                },
                new Button("Cancel") {Clicked = Application.RequestStop})
            {
                scrollView
            });
        }

        private static void DisplayMessage(string title, string message)
        {
            var resultStringLines = message.Split(Environment.NewLine);

            var resultsView = new ScrollView(new Rect(5, 1, 50, 6))
            {
                ContentSize = new Size(resultStringLines.OrderByDescending(line => line.Length).First().Length,
                    resultStringLines.Length),
                ShowVerticalScrollIndicator = true,
                ShowHorizontalScrollIndicator = true
            };
            resultsView.Add(new Label(message));

            Application.Run(new Dialog(title, 60, 13,
                new Button("OK") {Clicked = Application.RequestStop})
            {
                resultsView
            });
        }
    }
}

