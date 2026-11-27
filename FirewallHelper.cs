using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KTLanDrop
{
    public static class FirewallHelper
    {
        public static string GetRuleName(int port)
        {
            return "KTLanDrop_LAN_" + port;
        }

        public static string GetBestLanIp()
        {
            string fallback = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool isVirtual = desc.Contains("meta") || desc.Contains("clash") || desc.Contains("wintun") ||
                                 desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") ||
                                 desc.Contains("vethernet") || desc.Contains("tap-") || desc.Contains("vpn");

                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.") || ip.StartsWith("198.18.")) continue;

                    if (!isVirtual && (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.")))
                        return ip;

                    if (fallback == null && !isVirtual) fallback = ip;
                }
            }
            return fallback ?? "127.0.0.1";
        }

        public static bool IsFirewallRulePresent(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + GetRuleName(port) + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return p.ExitCode == 0 && output.IndexOf(GetRuleName(port), StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool AddLanFirewallRuleWithUac(int port, out string error)
        {
            error = null;
            string ruleName = GetRuleName(port);
            string args = string.Format(
                "/c netsh advfirewall firewall delete rule name=\"{0}\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol=TCP localport={1} remoteip=localsubnet profile=any",
                ruleName, port);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", args)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    if (p.ExitCode != 0)
                    {
                        error = "防火墙规则添加返回非零状态码: " + p.ExitCode;
                        return false;
                    }
                    return IsFirewallRulePresent(port);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
