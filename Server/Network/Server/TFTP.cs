﻿namespace bootpd
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Net;
	using System.Threading;

	public sealed class TFTP : ServerProvider, ITFTPServer_Provider, IDisposable
	{
		public static Dictionary<IPAddress, TFTPClient> Clients = new Dictionary<IPAddress, TFTPClient>();

		public static Dictionary<string, string> Options = new Dictionary<string, string>();

		TFTPSocket socket;
		FileStream fs;
		BufferedStream bs;

		public TFTP(IPEndPoint endpoint)
		{
			this.endp = endpoint;
			this.socket = new TFTPSocket(this.endp);
			this.socket.DataReceived += this.DataReceived;
			this.socket.DataSend += this.DataSend;

			Directories.Create(Settings.TFTPRoot);
		}

		~TFTP()
		{
			this.Dispose();
		}

		public override IPEndPoint LocalEndPoint
		{
			get
			{
				return this.endp;
			}

			set
			{
				this.endp = value;
			}
		}

		public override SocketType Type
		{
			get
			{
				return this.type;
			}

			set
			{
				this.type = value;
			}
		}

		public void Dispose()
		{
			Clients.Clear();
			Options.Clear();

			if (this.fs != null)
			{
				this.fs.Close();
				this.fs.Dispose();
			}

			if (this.bs != null)
			{
				this.bs.Close();
				this.bs.Dispose();
			}

			this.socket.Dispose();
		}

		public void Handle_ACK_Request(object data)
		{
			var packet = (TFTPPacket)data;

			if (!Clients.ContainsKey(packet.Source.Address) || string.IsNullOrEmpty(Clients[packet.Source.Address].FileName))
				return;

			if (packet.Block == Clients[packet.Source.Address].Blocks)
			{
				Clients[packet.Source.Address].Blocks += 1;

				this.Readfile(packet.Source);
			}
		}

		public void Handle_Error_Request(TFTPErrorCode error, string message, IPEndPoint client, bool clientError = false)
		{
			if (!Clients.ContainsKey(client.Address))
				return;

			Clients[client.Address].Stage = TFTPStage.Error;

			if (!clientError)
			{
				var response = new TFTPPacket(5 + message.Length, TFTPOPCodes.ERR, client);
				response.ErrorCode = error;
				response.ErrorMessage = message;

				this.Send(ref response);
			}

			Errorhandler.Report(LogTypes.Error, "[TFTP] {0}: {1}".F(error, message));

			if (this.fs != null)
				this.fs.Close();

			if (this.bs != null)
				this.bs.Close();

			Clients.Remove(client.Address);
		}

		public void Send(ref TFTPPacket packet)
		{
			if (Clients.ContainsKey(packet.Source.Address))
				this.socket.Send(packet.Source, packet);
		}

		public void Handle_RRQ_Request(object request)
		{
			var packet = (TFTPPacket)request;

			if (!Clients.ContainsKey(packet.Source.Address))
				Clients.Add(packet.Source.Address, new TFTPClient(packet.Source));
			else
				Clients[packet.Source.Address].EndPoint = packet.Source;

			this.ExtractOptions(packet);

			if (!Clients.ContainsKey(packet.Source.Address))
				return;

			Clients[packet.Source.Address].Stage = TFTPStage.Handshake;
			Clients[packet.Source.Address].Blocks = 0;

			var file = Filesystem.ResolvePath(Options["file"]);
			if (Filesystem.Exist(file) && !string.IsNullOrEmpty(file))
			{
				Clients[packet.Source.Address].FileName = file;
				this.fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.ReadBuffer);
				this.bs = new BufferedStream(this.fs, Settings.ReadBuffer);

				Clients[packet.Source.Address].TransferSize = fs.Length;

				if (Options.ContainsKey("blksize"))
				{
					this.Handle_Option_request(Clients[packet.Source.Address].TransferSize,
					Clients[packet.Source.Address].BlockSize, Clients[packet.Source.Address].EndPoint);

					return;
				}

				Clients[packet.Source.Address].Stage = TFTPStage.Transmitting;

				this.Readfile(Clients[packet.Source.Address].EndPoint);
				Options.Clear();
			}
			else
			{
				this.Handle_Error_Request(TFTPErrorCode.FileNotFound, file, packet.Source);

				return;
			}
		}

		public void SetMode(TFTPMode mode, IPEndPoint client)
		{
			if (mode != TFTPMode.Octet)
				this.Handle_Error_Request(TFTPErrorCode.InvalidOption, "Invalid Option", client);
			else
			{
				if (Clients.ContainsKey(client.Address))
					Clients[client.Address].Mode = mode;
			}
		}

		internal void ExtractOptions(TFTPPacket data)
		{
			var parts = Exts.ToParts(data.Data, "\0".ToCharArray());

			for (var i = 0; i < parts.Length; i++)
			{
				if (i == 0)
				{
					if (!Options.ContainsKey("file"))
						Options.Add("file", parts[i]);
					else
						Options["file"] = parts[i];
				}

				if (i == 1)
				{
					if (!Options.ContainsKey("mode"))
						Options.Add("mode", parts[i]);
					else
						Options["mode"] = parts[i];

					this.SetMode(TFTPMode.Octet, data.Source);
				}

				if (parts[i] == "blksize")
				{
					if (!Options.ContainsKey(parts[i]))
						Options.Add(parts[i], parts[i + 1]);
					else
						Options[parts[i]] = parts[i + 1];

					if (Clients.ContainsKey(data.Source.Address))
						Clients[data.Source.Address].BlockSize = int.Parse(Options["blksize"]);
				}

				if (parts[i] == "tsize")
				{
					if (!Options.ContainsKey(parts[i]))
						Options.Add(parts[i], parts[i + 1]);
					else
						Options[parts[i]] = parts[i + 1];
				}

				if (parts[i] == "windowsize")
				{
					if (!Options.ContainsKey(parts[i]))
						Options.Add(parts[i], parts[i + 1]);
					else
						Options[parts[i]] = parts[i + 1];
				}
			}
		}

		internal override void DataReceived(object sender, DataReceivedEventArgs e)
		{
			lock (this)
			{
				var request = new TFTPPacket(e.Data.Length, TFTPOPCodes.UNK, e.RemoteEndpoint);
				request.Data = e.Data;

				request.Type = SocketType.TFTP;

				switch (request.OPCode)
				{
					case TFTPOPCodes.RRQ:
						var rrq_thread = new Thread(new ParameterizedThreadStart(Handle_RRQ_Request));
						rrq_thread.Start(request);
						break;
					case TFTPOPCodes.ERR:
						this.Handle_Error_Request(request.ErrorCode, request.ErrorMessage, request.Source, true);
						break;
					case TFTPOPCodes.ACK:
						if (!Clients.ContainsKey(request.Source.Address))
							return;

						var ack_thread = new Thread(new ParameterizedThreadStart(Handle_ACK_Request));
						ack_thread.Start(request);
						break;
					default:
						this.Handle_Error_Request(TFTPErrorCode.IllegalOperation, "Unknown OPCode: {0}".F(request.OPCode), request.Source);
						break;
				}
			}
		}

		internal override void DataSend(object sender, DataSendEventArgs e)
		{
			if (Clients.ContainsKey(e.RemoteEndpoint.Address) && Clients[e.RemoteEndpoint.Address].Stage == TFTPStage.Done)
			{
				Clients.Remove(e.RemoteEndpoint.Address);

				if (this.fs != null)
					this.fs.Close();

				if (this.bs != null)
					this.bs.Close();
			}
		}

		internal void Handle_Option_request(long tsize, int blksize, IPEndPoint client)
		{
			if (!Clients.ContainsKey(client.Address))
				return;

			Clients[client.Address].Stage = TFTPStage.Handshake;

			var tmpbuffer = new byte[100];
			var offset = 0;

			var blksizeopt = Exts.StringToByte("blksize");
			Array.Copy(blksizeopt, 0, tmpbuffer, offset, blksizeopt.Length);
			offset += blksizeopt.Length + 1;

			var blksize_value = Exts.StringToByte(blksize.ToString());
			Array.Copy(blksize_value, 0, tmpbuffer, offset, blksize_value.Length);
			offset += blksize_value.Length + 1;

			var tsizeOpt = Exts.StringToByte("tsize");
			Array.Copy(tsizeOpt, 0, tmpbuffer, offset, tsizeOpt.Length);
			offset += tsizeOpt.Length + 1;

			var tsize_value = Exts.StringToByte(tsize.ToString());
			Array.Copy(tsize_value, 0, tmpbuffer, offset, tsize_value.Length);
			offset += tsize_value.Length + 1;

			var packet = new TFTPPacket(2 + offset, TFTPOPCodes.OCK, client);
			Array.Copy(tmpbuffer, 0, packet.Data, packet.Offset, offset);
			packet.Offset += offset;

			Array.Clear(tmpbuffer, 0, tmpbuffer.Length);

			this.Send(ref packet);
		}

		internal void Readfile(IPEndPoint client)
		{
			var readedBytes = 0L;
			var done = false;

			if (this.fs == null)
				this.fs = new FileStream(Clients[client.Address].FileName,
				 FileMode.Open, FileAccess.Read, FileShare.Read, Settings.ReadBuffer);

			if (this.bs == null)
				this.bs = new BufferedStream(this.fs, Settings.ReadBuffer);

			// Align the last Block
			if (Clients[client.Address].TransferSize <= Clients[client.Address].BlockSize)
			{
				Clients[client.Address].BlockSize = (int)Clients[client.Address].TransferSize;
				done = true;
			}

			var chunk = new byte[Clients[client.Address].BlockSize];

			this.bs.Seek(Clients[client.Address].BytesRead, SeekOrigin.Begin);
			readedBytes = this.bs.Read(chunk, 0, chunk.Length);

			Clients[client.Address].BytesRead += readedBytes;
			Clients[client.Address].TransferSize -= readedBytes;

			var response = new TFTPPacket(4 + chunk.Length, TFTPOPCodes.DAT, client);

			if (Clients[client.Address].Blocks == 0)
				Clients[client.Address].Blocks += 1;

			response.Block = Convert.ToInt16(Clients[client.Address].Blocks);
			Array.Copy(chunk, 0, response.Data, response.Offset, chunk.Length);
			response.Offset += chunk.Length;

			this.Send(ref response);

			if (Clients.ContainsKey(client.Address) && done)
				Clients[client.Address].Stage = TFTPStage.Done;
		}
	}
}