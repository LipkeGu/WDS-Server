﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Server.Crypto;
using static Server.Extensions.Functions;
using Server.Extensions;
using System.IO;

namespace Server.Network
{
	public static class Functions
	{
		public static void ReadServerList(ref SQLDatabase db, ref Dictionary<string, Serverentry<ushort>> servers)
		{
			var s = db.SQLQuery("SELECT * FROM ServerList LIMIT {0}".F((byte.MaxValue - 1)));
			for (var i = ushort.MinValue; i < s.Count; i++)
				if (!servers.ContainsKey(s[i]["HostName"]))
				{
					var entry = new Serverentry<ushort>(i, s[i]["HostName"], s[i]["BootFile"], IPAddress.Parse(s[i]["Address"]));
					servers.Add(s[i]["HostName"], entry);
				}

			if (!servers.ContainsKey(Settings.ServerName))
				servers.Add(Settings.ServerName, new Serverentry<ushort>(254, Settings.ServerName, Settings.DHCP_DEFAULT_BOOTFILE,
				Settings.ServerIP, BootServerTypes.MicrosoftWindowsNTBootServer));
		}

		public static ushort CalcBlocksize(long tsize, ushort blksize)
		{
			var res = tsize / blksize;
			if (res < ushort.MaxValue)
				return blksize;
			else
			{
				if (res <= blksize)
					return Convert.ToUInt16(res);
				else
					return blksize;
			}
		}

		public static byte[] GenerateServerList(ref Dictionary<string, Serverentry<ushort>> servers, ushort item)
		{
			if (item == 0)
			{
				var discover = new byte[3];
				discover[0] = Convert.ToByte(PXEVendorEncOptions.DiscoveryControl);
				discover[1] = Convert.ToByte(sizeof(byte));
				discover[2] = Convert.ToByte(2);

				#region "Menu Prompt"
				var msg = Settings.DHCP_MENU_PROMPT;

				if (msg.Length >= byte.MaxValue)
					msg.Remove(250);

				var message = Exts.StringToByte(msg, Encoding.ASCII);
				var timeout = byte.MaxValue;

				var prompt = new byte[(message.Length + 3)];
				prompt[0] = Convert.ToByte(PXEVendorEncOptions.MenuPrompt);
				prompt[1] = Convert.ToByte(message.Length + 1);
				prompt[2] = timeout;

				CopyTo(ref message, 0, ref prompt, 3, message.Length);
				#endregion

				#region "Menu"
				var menu = new byte[(servers.Count * 128)];
				var menulength = 0;
				var moffset = 0;
				var isrv2 = 0;

				foreach (var server in servers)
				{
					if (isrv2 > byte.MaxValue)
						break;

					var name = Exts.StringToByte("{0} ({1})".F(server.Value.Hostname, server.Value.IPAddress), Encoding.ASCII);
					var ident = BitConverter.GetBytes(server.Value.Ident);
					var nlen = name.Length;

					if (nlen > 128)
						nlen = 128;

					var menuentry = new byte[(ident.Length + nlen + 3)];
					moffset = CopyTo(ref ident, 0, ref menuentry, moffset, ident.Length);

					menuentry[2] = Convert.ToByte(nlen);
					moffset += 1;
					moffset += CopyTo(ref name, 0, ref menuentry, moffset, nlen);

					if (menulength == 0)
						menulength = 2;

					menulength += CopyTo(ref menuentry, 0, ref menu, menulength, moffset);
					isrv2++;
				}

				menu[0] = Convert.ToByte(PXEVendorEncOptions.BootMenue);
				menu[1] += Convert.ToByte(menulength - 2);
				#endregion

				#region "Serverlist"
				var entry = new byte[7];
				var srvlist = new byte[((servers.Count * entry.Length) + 2)];

				var resultoffset = 2;
				var isrv = 0;

				foreach (var server in servers)
				{
					if (isrv > byte.MaxValue)
						break;

					var entryoffset = 0;
					#region "Server entry"
					var ident = BitConverter.GetBytes(server.Value.Ident);
					var type = BitConverter.GetBytes(Convert.ToByte(server.Value.Type));
					var addr = Settings.ServerIP.GetAddressBytes();

					entryoffset += CopyTo(ref ident, 0, ref entry, entryoffset, ident.Length);
					entryoffset += CopyTo(ref type, 0, ref entry, entryoffset, 1);
					entryoffset += CopyTo(ref addr, 0, ref entry, entryoffset, addr.Length);

					resultoffset += CopyTo(ref entry, 0, ref srvlist, resultoffset, entry.Length);
					#endregion

					srvlist[0] = Convert.ToByte(PXEVendorEncOptions.BootServers);
					srvlist[1] += Convert.ToByte(entry.Length);
					#endregion

					isrv++;
				}

				var result = new byte[(discover.Length + menu.Length + prompt.Length + srvlist.Length)];
				var optoffset = 0;

				optoffset += CopyTo(ref discover, 0, ref result, optoffset, discover.Length);
				optoffset += CopyTo(ref srvlist, 0, ref result, optoffset, srvlist.Length);
				optoffset += CopyTo(ref prompt, 0, ref result, optoffset, prompt.Length);
				optoffset += CopyTo(ref menu, 0, ref result, optoffset, menulength);

				var block = new byte[optoffset];
				CopyTo(ref result, 0, ref block, 0, block.Length);

				return block;
			}
			else
			{
				var bootitem = new byte[6];
				bootitem[0] = Convert.ToByte(PXEVendorEncOptions.BootItem);

				var itm = BitConverter.GetBytes(item);
				bootitem[1] = Convert.ToByte(4);

				CopyTo(ref itm, 0, ref bootitem, 2, itm.Length);

				return bootitem;
			}
		}

		/// <summary>
		/// Returns the offset of the specified DHCP option in the Packet.
		/// </summary>
		/// <param name="packet">The DHCP Packet</param>
		/// <param name="option">The DHCP Option</param>
		/// <returns>This function returns 0 if the DHCP option is not in the packet.</returns>
		public static int GetOptionOffset(ref DHCPPacket packet, DHCPOptionEnum option)
		{
			var opt = Convert.ToInt32(option);
			for (var i = 0; i < packet.Data.Length; i++)
				if (packet.Data[i] == opt)
					return i;

			return 0;
		}

		public static void ParseNegotiatedFlags(NTLMFlags flags, ref RISClient client)
		{
			client.NTLMSSP_REQUEST_TARGET = flags.HasFlag(NTLMFlags.NTLMSSP_REQUEST_TARGET);
			client.NTLMSSP_NEGOTIATE_OEM = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_OEM);
			client.NTLMSSP_NEGOTIATE_UNICODE = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_UNICODE);
			client.NTLMSSP_NEGOTIATE_SEAL = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_SEAL);
			client.NTLMSSP_NEGOTIATE_SIGN = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_SIGN);
			client.NTLMSSP_NEGOTIATE_ALWAYS_SIGN = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_ALWAYS_SIGN);
			client.NTLMSSP_NEGOTIATE_DOMAIN_SUPPLIED = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_OEM_DOMAIN_SUPPLIED);
			client.NTLMSSP_NEGOTIATE_WORKSTATION_SUPPLIED = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_OEM_WORKSTATION_SUPPLIED);
			client.NTLMSSP_NEGOTIATE_56 = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_128 | NTLMFlags.NTLMSSP_NEGOTIATE_56);
			client.NTLMSSP_NEGOTIATE_128 = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_128 | NTLMFlags.NTLMSSP_NEGOTIATE_56);
			client.NTLMSSP_NEGOTIATE_LM_KEY = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_LM_KEY);
			client.NTLMSSP_NEGOTIATE_KEY_EXCH = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_KEY_EXCH);
			client.NTLMSSP_TARGET_TYPE_DOMAIN = flags.HasFlag(NTLMFlags.NTLMSSP_TARGET_TYPE_DOMAIN);
			client.NTLMSSP_TARGET_TYPE_SERVER = flags.HasFlag(NTLMFlags.NTLMSSP_TARGET_TYPE_SERVER);
			client.NTLMSSP_NEGOTIATE_NTLM = flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_EXTENDED_SESSIONSECURITY) ?
				false : flags.HasFlag(NTLMFlags.NTLMSSP_NEGOTIATE_NTLM);
		}

		public static byte[] ParameterlistEntry(string name, string type, string value)
		{
			var n = Exts.StringToByte(name, Encoding.ASCII);
			var t = Exts.StringToByte(type, Encoding.ASCII);
			var v = Exts.StringToByte(value, Encoding.ASCII);

			var data = new byte[n.Length + t.Length + v.Length + 2];

			var offset = 0;
			offset += CopyTo(ref n, 0, ref data, offset, n.Length) + 1;
			offset += CopyTo(ref t, 0, ref data, offset, t.Length) + 1;

			CopyTo(ref v, 0, ref data, offset, v.Length);

			return data;
		}

		public static void SelectBootFile(ref DHCPClient client)
		{
			var bootfile = string.Empty;
			var bcdpath = string.Empty;

			if (client.IsWDSClient)
				switch (client.Arch)
				{
					case Architecture.Intelx86PC:
						if (client.NextAction == NextActionOptionValues.Approval)
						{
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.WDS_BOOTFILE_X86);
							bcdpath = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.WDS_BCD_FileName);
						}
						else
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.WDS_BOOTFILE_ABORT);

						break;
					case Architecture.EFIItanium:
						if (client.NextAction == NextActionOptionValues.Approval)
						{
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_IA64, Settings.WDS_BOOTFILE_IA64);
							bcdpath = Path.Combine(Settings.WDS_BOOT_PREFIX_IA64, Settings.WDS_BCD_FileName);
						}
						else
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_IA64, Settings.WDS_BOOTFILE_ABORT);

						break;
					case Architecture.EFIx8664:
						if (client.NextAction == NextActionOptionValues.Approval)
						{
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X64, Settings.WDS_BOOTFILE_X64);
							bcdpath = Path.Combine(Settings.WDS_BOOT_PREFIX_X64, Settings.WDS_BCD_FileName);
						}
						else
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X64, Settings.WDS_BOOTFILE_ABORT);

						break;
					case Architecture.EFIBC:
						if (client.NextAction == NextActionOptionValues.Approval)
						{
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_EFI, Settings.WDS_BOOTFILE_EFI);
							bcdpath = Path.Combine(Settings.WDS_BOOT_PREFIX_EFI, Settings.WDS_BCD_FileName);
						}
						else
							bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_EFI, Settings.WDS_BOOTFILE_ABORT);

						break;
					default:
						bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.WDS_BOOTFILE_X86);
						bcdpath = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.WDS_BCD_FileName);
						break;
				}
			else
				bootfile = Path.Combine(Settings.WDS_BOOT_PREFIX_X86, Settings.DHCP_DEFAULT_BOOTFILE);


			if (Filesystem.Exist(Filesystem.ResolvePath(bootfile, Settings.TFTPRoot)))
			{
				client.BootFile = bootfile;
				client.BCDPath = bcdpath;
			}
			else
				Errorhandler.Report(LogTypes.Error, "File not Found: {0}".F(bootfile));
		}

		public static byte[] GenerateDHCPEncOption(int option, int length, byte[] data)
		{
			var o = BitConverter.GetBytes(Convert.ToByte(option));
			var l = BitConverter.GetBytes(Convert.ToByte(length));

			var result = new byte[(2 + data.Length)];

			var offset = 0;

			offset += CopyTo(ref o, 0, ref result, offset, sizeof(byte));
			offset += CopyTo(ref l, 0, ref result, offset, sizeof(byte));

			CopyTo(ref data, 0, ref result, offset, data.Length);

			return result;
		}

		public static sbyte GetTFTPOPCode(TFTPPacket packet) => GetTFTPOPCode(packet.Data);

		public static sbyte GetTFTPOPCode(byte[] packet) => Convert.ToSByte(packet[1]);

		public static bool IsTFTPOPCode(sbyte opcode, byte[] packet)
		{
			var code = GetTFTPOPCode(packet);

			return code != opcode ? false : true;
		}

		public static int FindEndOption(ref byte[] data)
		{
			for (var i = data.Length - 1; i > 0; i--)
				if (data[i] == byte.MaxValue)
					return i;

			return 0;
		}

		public static byte[] Unpack_Packet(ref RISPacket packet)
		{
			var data = new byte[packet.Length];
			CopyTo(packet.Data, 8, data, 0, data.Length);

			return data;
		}

		public static RISPacket Pack_Packet(RISPacket data)
		{
			var packet = new RISPacket(Encoding.ASCII, new byte[(data.Length + 8)]);
			CopyTo(data.Data, 0, packet.Data, 8, data.Length);

			return data;
		}
	}
}
