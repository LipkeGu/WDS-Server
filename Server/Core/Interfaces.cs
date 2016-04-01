﻿namespace bootpd
{
	using System;

	interface IDHCPServer_Provider
	{
		void Handle_DHCP_Request(DHCPPacket data, ref DHCPClient client);
	}

	interface IDHCPClient_Provider
	{
		Guid Guid
		{
			get; set;
		}

		Definitions.DHCPMsgType MsgType
		{
			get; set;
		}

		string BootFile
		{
			get; set;
		}
	}

	interface ITFTPServer_Provider
	{
		void Handle_RRQ_Request(object packet);

		void Handle_ACK_Request(object data);
	}
}
