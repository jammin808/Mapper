# libomtnet patches (PlayMap)

Upstream: https://github.com/openmediatransport/libomtnet at `7d29031` (13 Oct 2026, "Discovery improvements and fixes on
Windows"), version 1.0.0.20. Vendored as source under `src/` with the upstream `LICENSE.txt`; built as
`PlayMap.ThirdParty.Omt` for the repository's runtime. Every change is marked `PlayMap P<n>` in the source; a re-vendor
keeps this list true (the OMT tests reproduce the conditions each patch is for: no avahi-daemon, no IPv6, a receiver leaving as its sender closes).

| # | Where | Defect | Patch |
|---|---|---|---|
| P1 | `linux/OMTDiscoveryAvahi.cs`, `OMTDiscovery.cs` | With no avahi-daemon, `avahi_client_new` fails and the constructor goes on to `avahi_service_browser_new(NULL…)`; registering a sender calls `avahi_entry_group_new(NULL…)`: both assert and abort the process. Without the avahi client library the constructor throws. | Return early when the client is null (no browser, no event thread); `RegisterAddressInternal` answers false; a missing library falls back to the base discovery (servers and URLs). |
| P2 | `OMTUtils.cs`, `OMTSend.cs`, `OMTMultiConnectSocket.cs` | Senders, the metadata server and connections make `InterNetworkV6` dual-stack sockets unconditionally: `SocketException 97` on hosts without IPv6. | `OMTUtils.NewTcpSocket()`: dual-stack where `Socket.OSSupportsIPv6`, IPv4 otherwise; binds use the family's wildcard. |
| P3 | `OMTUtils.cs`, `OMTMultiConnectSocket.cs` | Addresses are kept v4-mapped; connecting an IPv4 socket to one throws `NotSupportedException`. | Connect to `MapToIPv4()` on an IPv4 socket; skip a true IPv6 address there. |
| P4 | `server/OMTDiscoveryServer.cs` | The discovery server writes to the process's stdout. | Through `OMTLogging.Write`. |
| P5 | (configuration, no source change) | Settings and logs land under the user's OMT folder. | PlayMap sets `OMT_STORAGE_PATH` to the node's own folder, the discovery server and port range in memory (never `Save()`), and turns file logging off before first use. |
| P6 | `linux/OMTDiscoveryAvahi.cs` | Stopping discovery calls `Thread.Abort` (SYSLIB0006), which throws `PlatformNotSupportedException` on .NET. | A volatile stop flag, a bounded `avahi_simple_poll_iterate(…, 250)` and `Join` with a timeout. |
| P7 | `OMTSend.cs` | `OMTSend.Dispose` holds the channel list's lock while it disposes each channel (which takes the channel's lock); a channel's receive callback holds the channel's lock while it reports a disconnect (which takes the list's lock): a receiver leaving while its sender is disposed deadlocked both threads (found by the loopback tests). | The channels are taken under the list's lock and disposed after it; `RemoveChannel` tolerates a list already taken. |

## The diff against upstream

```diff
diff --git a/src/OMTDiscovery.cs b/src/OMTDiscovery.cs
index 36c560e..f89147c 100644
--- a/src/OMTDiscovery.cs
+++ b/src/OMTDiscovery.cs
@@ -91,7 +91,19 @@ namespace libomtnet
                             instance = new mac.OMTDiscoveryMac();
                             break;
                         case OMTPlatformType.Linux:
-                            instance = new linux.OMTDiscoveryAvahi();
+                            // PlayMap P1: a machine without the avahi client library discovers through a server or URLs only.
+                            try
+                            {
+                                instance = new linux.OMTDiscoveryAvahi();
+                            }
+                            catch (DllNotFoundException)
+                            {
+                                instance = new OMTDiscovery();
+                            }
+                            catch (EntryPointNotFoundException)
+                            {
+                                instance = new OMTDiscovery();
+                            }
                             break;
                         default:
                             instance = new OMTDiscovery();
diff --git a/src/OMTMultiConnectSocket.cs b/src/OMTMultiConnectSocket.cs
index 34014f5..ec2f691 100644
--- a/src/OMTMultiConnectSocket.cs
+++ b/src/OMTMultiConnectSocket.cs
@@ -63,9 +63,14 @@ namespace libomtnet
                     Socket s = null;
                     try
                     {
-                        s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
-                        s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
-                        IAsyncResult ar = s.BeginConnect(next, port, null, null);
+                        s = OMTUtils.NewTcpSocket(); // PlayMap P2
+                        IPAddress target = OMTUtils.ConnectAddressFor(s, next); // PlayMap P3
+                        if (target == null)
+                        {
+                            lastError = new NotSupportedException("No IPv6 on this host for " + next.ToString());
+                            continue;
+                        }
+                        IAsyncResult ar = s.BeginConnect(target, port, null, null);
                         if (ar.AsyncWaitHandle.WaitOne(timeoutMilliseconds, false))
                         {
                             s.EndConnect(ar);
diff --git a/src/OMTSend.cs b/src/OMTSend.cs
index ab7ad79..5c808ae 100644
--- a/src/OMTSend.cs
+++ b/src/OMTSend.cs
@@ -68,8 +68,11 @@ namespace libomtnet
             metadataHandle = new AutoResetEvent(false);
             listenEvent = new SocketAsyncEventArgs();
             listenEvent.Completed += OnAccept;
-            this.listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
-            this.listener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
+            this.listener = OMTUtils.NewTcpSocket(); // PlayMap P2
+            if (this.listener.AddressFamily == AddressFamily.InterNetwork && endpoint.AddressFamily == AddressFamily.InterNetworkV6)
+            {
+                endpoint = new IPEndPoint(IPAddress.Any, endpoint.Port);
+            }
             this.listener.Bind(endpoint);
             this.listener.Listen(5);
             BeginAccept();
@@ -94,7 +97,7 @@ namespace libomtnet
             this.discovery = OMTDiscovery.GetInstance();
             this.quality = quality;
             this.suggestedQuality = quality;
-            this.listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
+            this.listener = OMTUtils.NewTcpSocket(); // PlayMap P2
 
             OMTSettings settings = OMTSettings.GetInstance();
             int startPort = settings.GetInteger("NetworkPortStart", OMTConstants.NETWORK_PORT_START);
@@ -104,8 +107,7 @@ namespace libomtnet
             {
                 try
                 {
-                    this.listener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
-                    this.listener.Bind(new IPEndPoint(IPAddress.IPv6Any, i));
+                    this.listener.Bind(new IPEndPoint(OMTUtils.AnyFor(this.listener), i)); // PlayMap P2
                     this.listener.Listen(5);
                     break;
                 }
@@ -275,19 +277,24 @@ namespace libomtnet
                 listenEvent.Dispose();
                 listenEvent = null;
             }
+            // PlayMap P7: the channels are taken under the list's lock and disposed after it is let go. A channel's receive callback
+            // holds the channel's lock while it reports a disconnect, which takes this lock (RemoveChannel); disposing a channel
+            // under this lock waits for the channel's: the two deadlocked when a receiver left while the sender was disposed.
+            OMTChannel[] closing;
             lock (channelsLock)
             {
-                if (channels != null)
+                closing = channels;
+                channels = null;
+            }
+            if (closing != null)
+            {
+                foreach (OMTChannel channel in closing)
                 {
-                    foreach (OMTChannel channel in channels)
+                    if (channel != null)
                     {
-                        if (channel != null)
-                        {
-                            channel.Changed -= Channel_Changed;
-                            channel.Dispose();
-                        }
+                        channel.Changed -= Channel_Changed;
+                        channel.Dispose();
                     }
-                    channels = null;
                 }
             }
             if (codec != null)
@@ -431,7 +438,8 @@ namespace libomtnet
         {
             lock (channelsLock)
             {
-                if (channel != null)
+                // PlayMap P7: a disconnect reported while the sender is disposed finds the list already taken.
+                if (channel != null && channels != null)
                 {
                     List<OMTChannel> list = new List<OMTChannel>();
                     list.AddRange(channels);
diff --git a/src/OMTUtils.cs b/src/OMTUtils.cs
index 7fcd589..05396bd 100644
--- a/src/OMTUtils.cs
+++ b/src/OMTUtils.cs
@@ -26,6 +26,7 @@
 using System;
 using System.IO;
 using System.Net;
+using System.Net.Sockets;
 using System.Runtime.InteropServices;
 using System.Text;
 
@@ -33,6 +34,35 @@ namespace libomtnet
 {
     public class OMTUtils
     {
+        // PlayMap P2: a TCP socket dual-stack where the OS has IPv6, IPv4 where it has not (an IPv6 socket on such a host throws
+        // "Address family not supported").
+        internal static Socket NewTcpSocket()
+        {
+            if (Socket.OSSupportsIPv6)
+            {
+                Socket s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
+                s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
+                return s;
+            }
+            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
+        }
+
+        // PlayMap P2: the wildcard address of a socket's family.
+        internal static IPAddress AnyFor(Socket s)
+        {
+            return s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
+        }
+
+        // PlayMap P3: the address a socket of this family connects to (a v4-mapped address as IPv4 on an IPv4 socket), or null
+        // when it cannot reach it.
+        internal static IPAddress ConnectAddressFor(Socket s, IPAddress address)
+        {
+            if (s.AddressFamily == AddressFamily.InterNetworkV6) return address;
+            if (address.AddressFamily == AddressFamily.InterNetwork) return address;
+            if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
+            return null;
+        }
+
         internal static IPAddress[] ResolveHostname(string hostname)
         {
             try
diff --git a/src/linux/OMTDiscoveryAvahi.cs b/src/linux/OMTDiscoveryAvahi.cs
index 6f01bdb..b264695 100644
--- a/src/linux/OMTDiscoveryAvahi.cs
+++ b/src/linux/OMTDiscoveryAvahi.cs
@@ -45,7 +45,7 @@ namespace libomtnet.linux
         private IntPtr serviceType;
 
         private Thread eventThread;
-        private bool eventThreadRunning = false;
+        private volatile bool eventThreadRunning = false;
 
         private class EntryAvahi : OMTDiscoveryEntry
         {
@@ -87,6 +87,9 @@ namespace libomtnet.linux
             if (client == IntPtr.Zero)
             {
                 OMTLogging.Write("Failure creating client: " + hr, "OMTDiscoveryAvahi");
+                // PlayMap P1: no avahi-daemon. avahi_service_browser_new and avahi_entry_group_new assert on a null client and
+                // abort the process; discovery falls back to a discovery server or addresses given by URL.
+                return;
             }
             browser = AvahiClient.avahi_service_browser_new(client, AvahiClient.AVAHI_IF_UNSPEC, AvahiClient.AVAHI_PROTO_UNSPEC
                 , serviceType, IntPtr.Zero, 0, Marshal.GetFunctionPointerForDelegate(serviceBrowserCallback), IntPtr.Zero);
@@ -108,7 +111,8 @@ namespace libomtnet.linux
                 int hr = 0;
                 while (eventThreadRunning)
                 {
-                    hr = AvahiClient.avahi_simple_poll_iterate(simplePoll, -1);
+                    // PlayMap P6: a bounded wait, so the loop sees the stop flag without Thread.Abort.
+                    hr = AvahiClient.avahi_simple_poll_iterate(simplePoll, 250);
                     if (hr != 0)
                     {
                         OMTLogging.Write("EventThead exiting...", "OMTDiscoveryAvahi");
@@ -132,10 +136,8 @@ namespace libomtnet.linux
             }
             if (eventThread != null)
             {
-                if (eventThread.Join(5000) == false)
-                {
-                    eventThread.Abort();
-                }
+                // PlayMap P6: Thread.Abort throws PlatformNotSupportedException on .NET; the loop ends at its next bounded wait.
+                eventThread.Join(5000);
                 eventThread = null;
             }
             if (browser != IntPtr.Zero)
@@ -178,6 +180,8 @@ namespace libomtnet.linux
         }
         internal override bool RegisterAddressInternal(OMTAddress address)
         {
+            // PlayMap P1: no avahi client, nothing to register with.
+            if (client == IntPtr.Zero) return false;
             lock (lockSync)
             {
                 OMTDiscoveryEntry entry = GetEntry(address);
diff --git a/src/server/OMTDiscoveryServer.cs b/src/server/OMTDiscoveryServer.cs
index 30bed8b..70ef6ce 100644
--- a/src/server/OMTDiscoveryServer.cs
+++ b/src/server/OMTDiscoveryServer.cs
@@ -120,7 +120,7 @@ namespace libomtnet
                 addresses.Add(entry);
                 SendEntry(entry, null);
                 OMTLogging.Write("Added " + address.ToString() + " From " + endpoint.ToString(), "OMTDiscoveryServer");
-                Console.WriteLine(endpoint.ToString() + " ADDED " + address.ToString());
+                OMTLogging.Write(endpoint.ToString() + " ADDED " + address.ToString(), "OMTDiscoveryServer"); // PlayMap P4
             }
         }
         private void RemoveEntry(AddressEntry entry, IPEndPoint endpoint)
@@ -131,7 +131,7 @@ namespace libomtnet
                 entry.Address.removed = true;
                 SendEntry(entry, null);
                 OMTLogging.Write("Removed " + entry.Address.ToString() + " From " + endpoint.ToString(), "OMTDiscoveryServer");
-                Console.WriteLine(endpoint.ToString() + " REMOVED " + entry.Address.ToString());
+                OMTLogging.Write(endpoint.ToString() + " REMOVED " + entry.Address.ToString(), "OMTDiscoveryServer"); // PlayMap P4
             }
         }
         private void SendEntry(AddressEntry entry, IPEndPoint endpoint)
@@ -154,7 +154,7 @@ namespace libomtnet
             try
             {
                 SendAllToEndpoint(endpoint);
-                Console.WriteLine("Connected: " + endpoint.ToString());
+                OMTLogging.Write("Connected: " + endpoint.ToString(), "OMTDiscoveryServer"); // PlayMap P4
             }
             catch (Exception ex)
             {
@@ -167,7 +167,7 @@ namespace libomtnet
             try
             {
                 RemoveEntriesByEndPoint(endpoint);
-                Console.WriteLine("Disconnected: " + endpoint.ToString());
+                OMTLogging.Write("Disconnected: " + endpoint.ToString(), "OMTDiscoveryServer"); // PlayMap P4
             }
             catch (Exception ex)
             {
```
