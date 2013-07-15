//
//  Server.cs
//  This file is part of XG - XDCC Grabscher
//  http://www.larsformella.de/lang/en/portfolio/programme-software/xg
//
//  Author:
//       Lars Formella <ich@larsformella.de>
//
//  Copyright (c) 2012 Lars Formella
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
//

using Fleck;

using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;

using XG.Core;
using XG.Server.Plugin.General.Webserver.Object;
using XG.Server.Worker;

using log4net;
using SharpRobin.Core;

namespace XG.Server.Plugin.General.Webserver.Websocket
{
	public class Server : ASaltedPassword
	{
		#region VARIABLES

		static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		WebSocketServer _webSocket;
		JsonSerializerSettings _jsonSerializerSettings;

		readonly HashSet<User> _users = new HashSet<User>();

		static readonly Core.Search _searchDownloads = new Core.Search{ Guid = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Downloads" };
		static readonly Core.Search _searchEnabled = new Core.Search { Guid = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "Enabled Packets" };

		public RrdDb RrdDb { get; set; }

		#endregion

		public Server()
		{
			_jsonSerializerSettings = new JsonSerializerSettings
			{
				DateFormatHandling = DateFormatHandling.MicrosoftDateFormat,
				DateParseHandling = DateParseHandling.DateTime,
				DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind
			};
			_jsonSerializerSettings.Converters.Add(new DoubleConverter());
		}

		#region AWorker

		protected override void StartRun ()
		{
			_webSocket = new WebSocketServer("ws://localhost:" + (Settings.Instance.WebServerPort + 1));
			//FleckLog.Level = LogLevel.Debug;

			_webSocket.Start (socket =>
			{
				socket.OnOpen = () => OnOpen(socket);
				socket.OnClose = () => OnClose(socket);
				socket.OnMessage = message => OnMessage(socket, message);
				socket.OnError = exception => OnError(socket, exception);
			});
		}

		protected override void StopRun()
		{
			//_webSocket.Stop();
		}

		#endregion

		#region REPOSITORY EVENTS

		protected override void ObjectAdded(Core.AObject aParent, Core.AObject aObj)
		{
			var response = new Response
			{
				Type = Response.Types.ObjectAdded,
				Data = aObj
			};
			Broadcast(response);
		}

		protected override void ObjectRemoved(Core.AObject aParent, Core.AObject aObj)
		{
			var response = new Response
			{
				Type = Response.Types.ObjectRemoved,
				Data = aObj
			};
			Broadcast(response);
		}

		protected override void ObjectChanged(Core.AObject aObj, string[] aFields)
		{
			BroadcastChanged(aObj);

			HashSet<string> fields = new HashSet<string>(aFields);

			// if a bot changed dispatch the packets, too
			if (aObj is Core.Bot)
			{
				if (fields.Contains("Connected"))
				{
					foreach (var pack in (aObj as Core.Bot).Packets)
					{
						BroadcastChanged(pack);
					}
				}
			}
			// if a part changed dispatch the file, packet and bot, too
			else if (aObj is FilePart)
			{
				var part = aObj as FilePart;
				BroadcastChanged(part.Parent);

				if (part.Packet != null)
				{
					if (fields.Contains("Speed") || fields.Contains("CurrentSize") || fields.Contains("TimeMissing"))
					{
						BroadcastChanged(part.Packet);
					}
					if (fields.Contains("Speed"))
					{
						BroadcastChanged(part.Packet.Parent);
					}
				}
			}
		}

		protected override void ObjectEnabledChanged(Core.AObject aObj)
		{
			BroadcastChanged(aObj);

			// if a packet changed dispatch the bot, too
			if (aObj is Core.Packet)
			{
				var part = aObj as Core.Packet;
				BroadcastChanged(part.Parent);
			}
		}

		protected override void FileAdded(Core.AObject aParent, Core.AObject aObj)
		{
			ObjectAdded(aParent, aObj);
		}

		protected override void FileRemoved(Core.AObject aParent, Core.AObject aObj)
		{
			ObjectRemoved(aParent, aObj);
		}

		protected override void FileChanged(Core.AObject aObj, string[] aFields)
		{
			ObjectChanged(aObj, aFields);
		}

		protected override void SearchAdded(Core.AObject aParent, Core.AObject aObj)
		{
			ObjectAdded(aParent, aObj);
		}

		protected override void SearchRemoved(Core.AObject aParent, Core.AObject aObj)
		{
			ObjectRemoved(aParent, aObj);
		}

		protected override void SearchChanged(Core.AObject aObj, string[] aFields)
		{
			ObjectChanged(aObj, aFields);
		}

		protected override void NotificationAdded(Core.AObject aParent, Core.AObject aObj)
		{
			ObjectAdded(aParent, aObj);
		}

		#endregion

		#region WebSocket

		void OnOpen(IWebSocketConnection aContext)
		{
			Log.Info("OnOpen(" + aContext.ConnectionInfo.ClientIpAddress + ")");

			var user = new User
			{
				Connection = aContext,
				LoadedObjects = new HashSet<Core.AObject>(),
				LastSearch = Guid.Empty
			};

			_users.Add(user);
		}

		void OnClose(IWebSocketConnection aContext)
		{
			Log.Info("OnClose(" + aContext.ConnectionInfo.ClientIpAddress + ")");

			foreach (var user in _users.ToArray())
			{
				if (user.Connection == aContext)
				{
					_users.Remove(user);
				}
			}
		}

		void OnMessage(IWebSocketConnection aContext, string aMessage)
		{
			Log.Info("OnMessage(" + aContext.ConnectionInfo.ClientIpAddress + ", " + aMessage + ")");

			var currentUser = (from user in _users where user.Connection == aContext select user).SingleOrDefault();
			var request = JsonConvert.DeserializeObject<Request>(aMessage);
#if !UNSAFE
			try
			{
#endif
				// no pass, no way
				if (request.Password != Password)
				{
					Log.Error("OnMessage(" + aContext.ConnectionInfo.ClientIpAddress + ") bad password");
					// exit
					return;
				}

				switch (request.Type)
				{
					case Request.Types.AddServer:
						string serverString = request.Name;
						int port = 6667;
						if (serverString.Contains(":"))
						{
							string[] serverArray = serverString.Split(':');
							serverString = serverArray[0];
							port = int.Parse(serverArray[1]);
						}
						AddServer(serverString, port);
						break;

					case Request.Types.RemoveServer:
						RemoveServer(request.Guid);
						break;

					case Request.Types.AddChannel:
						AddChannel(request.Guid, request.Name);
						break;

					case Request.Types.RemoveChannel:
						RemoveChannel(request.Guid);
						break;

					case Request.Types.ActivateObject:
						ActivateObject(request.Guid);
						break;

					case Request.Types.DeactivateObject:
						DeactivateObject(request.Guid);
						break;

					case Request.Types.Search:
						currentUser.LastSearch = request.Guid;
						var all = FilteredPacketsAndBotsByGuid(request.Guid, request.Name);
						UnicastOnRequest(currentUser, all, request.Type);
						Unicast(currentUser, new Response
						{
							Type = Response.Types.SearchComplete,
							Data = request.Type
						});

						// send search again to update search results
						var searchObj = Searches.WithGuid(request.Guid);
						if (searchObj != null)
						{
							Unicast(currentUser, new Response
							{
								Type = Response.Types.ObjectChanged,
								Data = searchObj
							});
						}
						break;

					case Request.Types.SearchExternal:
						var searchExternal = Searches.WithGuid(request.Guid);
						if (searchExternal != null)
						{
							request.Name = searchExternal.Name;
						}

						var results = SearchExternal(request.Name);
						foreach (var result in results)
						{
							var currentResponse = new Response
							{
								Type = Response.Types.ObjectAdded,
								Data = result
							};
							Unicast(currentUser, currentResponse);
						}

						Unicast(currentUser, new Response
						{
							Type = Response.Types.SearchComplete,
							Data = request.Type
						});
						break;

					case Request.Types.AddSearch:
						string name = request.Name;
						var obj = Searches.Named(name);
						if (obj == null)
						{
							obj = new Core.Search { Name = name };
							Searches.Add(obj);
						}
						break;

					case Request.Types.RemoveSearch:
						var search = Searches.WithGuid(request.Guid);
						if (search != null)
						{
							Searches.Remove(search);
						}
						break;

					case Request.Types.Searches:
						var searches = new List<Core.Search>();
						searches.AddRange(Searches.All);

						foreach (var currentSearch in searches)
						{
							Unicast(currentUser, new Response
							{
								Type = Response.Types.ObjectAdded,
								Data = currentSearch
							});
						}
						break;

					case Request.Types.Servers:
						UnicastOnRequest(currentUser, Servers.All, request.Type);
						break;

					case Request.Types.ChannelsFromServer:
						var channels = (from server in Servers.All from channel in server.Channels where channel.ParentGuid == request.Guid select channel).ToList();
						UnicastOnRequest(currentUser, channels, request.Type);
						var tServer = Servers.WithGuid(request.Guid);
						if (tServer != null)
						{
							Unicast(currentUser, new Response
							{
								Type = Response.Types.ObjectChanged,
								Data = tServer
							});
						}
						break;

					case Request.Types.PacketsFromBot:
						var botPackets = (from server in Servers.All
										  from channel in server.Channels
										  from bot in channel.Bots
										  from packet in bot.Packets
										  where packet.ParentGuid == request.Guid
										  select packet).ToList();
						UnicastOnRequest(currentUser, botPackets, request.Type);
						var tCurrentBot = Servers.WithGuid(request.Guid);
						if (tCurrentBot != null)
						{
							Unicast(currentUser, new Response
							{
								Type = Response.Types.ObjectChanged,
								Data = tCurrentBot
							});
						}
						break;

					case Request.Types.LiveSnapshot:
						Unicast(currentUser, new Response
						{
							Type = Response.Types.LiveSnapshot,
							Data = GetLatestSnapshot()
						});
						break;

					case Request.Types.Snapshots:
						var startTime = DateTime.Now.AddDays(int.Parse(request.Name));
						var data = GetFlotData(startTime, DateTime.Now);

						Unicast(currentUser, new Response
						{
							Type = Response.Types.Snapshots,
							Data = data
						});
						break;

					case Request.Types.Files:
						UnicastOnRequest(currentUser, Files.All, request.Type);
						break;

					case Request.Types.CloseServer:
						break;

					case Request.Types.ParseXdccLink:
						string[] link = request.Name.Substring(7).Split('/');
						string serverName = link[0];
						string channelName = link[2];
						string botName = link[3];
						int packetId = int.Parse(link[4].Substring(1));

						// checking server
						Core.Server serv = Servers.Server(serverName);
						if (serv == null)
						{
							Servers.Add(serverName);
							serv = Servers.Server(serverName);
						}
						serv.Enabled = true;

						// checking channel
						Core.Channel chan = serv.Channel(channelName);
						if (chan == null)
						{
							serv.AddChannel(channelName);
							chan = serv.Channel(channelName);
						}
						chan.Enabled = true;

						// checking bot
						Core.Bot tBot = chan.Bot(botName);
						if (tBot == null)
						{
							tBot = new Core.Bot { Name = botName };
							chan.AddBot(tBot);
						}

						// checking packet
						Core.Packet pack = tBot.Packet(packetId);
						if (pack == null)
						{
							pack = new Core.Packet { Id = packetId, Name = link[5] };
							tBot.AddPacket(pack);
						}
						pack.Enabled = true;
						break;
				}
#if !UNSAFE
			}
			catch (Exception ex)
			{
				Log.Fatal("OnMessage(" + aContext.ConnectionInfo.ClientIpAddress + ", " + aMessage + ")", ex);
			}
#endif
		}

		void OnError(IWebSocketConnection aContext, Exception aException)
		{
			Log.Info("OnError(" + aContext.ConnectionInfo.ClientIpAddress + ")", aException);

			OnClose(aContext);
		}

		void BroadcastChanged (Core.AObject aObj)
		{
			var response = new Response
			{
				Type = Response.Types.ObjectChanged,
				Data = aObj
			};
			Broadcast(response);
		}

		void UnicastOnRequest(User aUser, IEnumerable<object> aObjects, Request.Types aRequestType)
		{
			foreach (var obj in aObjects)
			{
				var response = new Response
				{
					Type = Response.Types.ObjectAdded,
					Data = obj
				};
				Unicast(aUser, response, false);
			}
		}

		void Broadcast(Response aResponse)
		{
			foreach (var user in _users.ToArray())
			{
				Unicast(user, aResponse);
			}
		}

		void Unicast(User aUser, Response aResponse, bool advancedVisibilityCheck = true)
		{
			if (aResponse.Data.GetType().IsSubclassOf(typeof(Core.AObject)))
			{
				// lock loaded objects to prevent sending out the same object more than once
				lock (aUser.LoadedObjects)
				{
					switch (aResponse.Type)
					{
						case Response.Types.ObjectAdded:
							if (aUser.LoadedObjects.Contains(aResponse.Data))
							{
								return;
							}
							if (advancedVisibilityCheck && (aResponse.Data is Core.Bot || aResponse.Data is Core.Packet) && !FilteredPacketsAndBotsByGuid(aUser.LastSearch).Contains(aResponse.Data))
							{
								return;
							}
							aUser.LoadedObjects.Add((Core.AObject)aResponse.Data);
							break;

						case Response.Types.ObjectChanged:
							if (!aUser.LoadedObjects.Contains(aResponse.Data))
							{
								return;
							}
							if (advancedVisibilityCheck && (aResponse.Data is Core.Bot || aResponse.Data is Core.Packet) && !FilteredPacketsAndBotsByGuid(aUser.LastSearch).Contains(aResponse.Data))
							{
								return;
							}
							break;

						case Response.Types.ObjectRemoved:
							if (!aUser.LoadedObjects.Contains(aResponse.Data))
							{
								return;
							}
							aUser.LoadedObjects.Remove((Core.AObject)aResponse.Data);
							break;
					}
				}
			}

			Object.AObject myObj = null;

			if (aResponse.Data is Core.Server)
			{
				myObj = new Object.Server { Object = aResponse.Data as Core.Server };
			}
			if (aResponse.Data is Core.Channel)
			{
				myObj = new Object.Channel { Object = aResponse.Data as Core.Channel };
			}
			if (aResponse.Data is Core.Bot)
			{
				myObj = new Object.Bot { Object = aResponse.Data as Core.Bot };
			}
			if (aResponse.Data is Core.Packet)
			{
				myObj = new Object.Packet { Object = aResponse.Data as Core.Packet };
			}
			if (aResponse.Data is Core.Search)
			{
				var results = FilteredPacketsAndBotsByGuid((aResponse.Data as Core.Search).Guid);
				myObj = new Object.Search
				{
					Object = aResponse.Data as Core.Search,
					Results = (from obj in results where obj is Core.Packet select obj).ToList().Count
				};
			}
			if (aResponse.Data is Core.Notification)
			{
				myObj = new Object.Notification { Object = aResponse.Data as Core.Notification };
			}
			if (aResponse.Data is Core.File)
			{
				myObj = new Object.File { Object = aResponse.Data as Core.File };
			}
			if (aResponse.Data is FilePart)
			{
				return;
			}

			if (myObj != null)
			{
				aResponse.Data = myObj;
			}

			string message = null;
			try
			{
				message = JsonConvert.SerializeObject(aResponse, _jsonSerializerSettings);
			}
			catch (Exception ex)
			{
				Log.Fatal("Unicast(" + aUser.Connection.ConnectionInfo.ClientIpAddress + ", " + aResponse.Type + "|" + aResponse.DataType + ")", ex);
			}

			if (message != null)
			{
#if !UNSAFE
				try
				{
#endif
					aUser.Connection.Send(message);
					Log.Info("Unicast(" + aUser.Connection.ConnectionInfo.ClientIpAddress + ", " + message + ")");
#if !UNSAFE
				}
				catch (Exception ex)
				{
					Log.Fatal("Unicast(" + aUser.Connection.ConnectionInfo.ClientIpAddress + ", " + message + ")", ex);
				}
#endif
			}
		}

		#endregion

		#region Functions

		private IEnumerable<Flot> GetLatestSnapshot ()
		{
			var flots = GetFlotData(DateTime.Now.AddMinutes(-15), DateTime.Now);
			foreach (var flot in flots)
			{
				double lastTime = 0;
				double lastValue = 0;
				foreach (var values in flot.Data)
				{
					if (values[1] >= 0)
					{
						if (values[0] > lastTime)
						{
							lastTime = values[0];
							lastValue = values[1];
						}
					}
				}
				flot.Data = new double[][] {new double[]{lastTime, lastValue}};
			}
			return flots;
		}

		private IEnumerable<ExternalSearch> SearchExternal (string search)
		{
			var objects = new List<ExternalSearch>();

			int start = 0;
			int limit = 25;
			do
			{
				try
				{
					var uri = new Uri("http://xg.bitpir.at/index.php?show=search&action=external&xg=" + Settings.Instance.XgVersion + "&start=" + start + "&limit=" + limit + "&search=" + search);
					var req = HttpWebRequest.Create(uri);

					var response = req.GetResponse();
					StreamReader sr = new StreamReader(response.GetResponseStream());
					string text = sr.ReadToEnd();
					response.Close();

					ExternalSearch[] results = JsonConvert.DeserializeObject<ExternalSearch[]>(text, _jsonSerializerSettings);

					if (results.Length > 0)
					{
						objects.AddRange(results);
					}

					if (results.Length == 0 || results.Length < limit)
					{
						break;
					}
				}
				catch (Exception ex)
				{
					Log.Fatal("SearchExternal(" + search + ") cant load external search", ex);
					break;
				}
				start += limit;
			} while (true);

			return objects;
		}

		List<Core.AObject> FilteredPacketsAndBotsByGuid (Guid aGuid, string aName = null)
		{
			var allBots = from server in Servers.All from channel in server.Channels from bot in channel.Bots select bot;
			var allPackets = (from bot in allBots from packet in bot.Packets select packet).ToList();

			if (aGuid == _searchDownloads.Guid)
			{
				allPackets = (from packet in allPackets where packet.Connected select packet).ToList();
			}
			else if (aGuid == _searchEnabled.Guid)
			{
				allPackets = (from packet in allPackets where packet.Enabled select packet).ToList();
			}
			else 
			{
				var search = Searches.WithGuid(aGuid);
				if (search != null)
				{
					aName = search.Name;
				}
				if (string.IsNullOrEmpty(aName))
				{
					aName = string.Empty;
					allPackets.Clear();
				}

				string[] searches = aName.ToLower().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
				allPackets = (from packet in allPackets where packet.Name.ToLower().ContainsAll(searches) select packet).ToList();
			}

			var bots = (from s in Servers.All from c in s.Channels from b in c.Bots join p in allPackets on b.Guid equals p.Parent.Guid select b).Distinct().ToList();
			var all = new List<Core.AObject>();
			all.AddRange(allPackets);
			all.AddRange(bots);
			return all;
		}

		Flot[] GetFlotData(DateTime aStart, DateTime aEnd)
		{
			var tObjects = new List<Flot>();

			FetchData data = RrdDb.createFetchRequest(ConsolFuns.CF_AVERAGE, aStart.ToTimestamp(), aEnd.ToTimestamp(), 1).fetchData();
			Int64[] times = data.getTimestamps();
			double[][] values = data.getValues();

			for (int a = 1; a <= 29; a++)
			{
				var value = (SnapshotValue) a;
				var obj = new Flot();

				var list = new List<double[]>();
				for (int b = 0; b < times.Length; b++)
				{
					double[] current = { times[b] * 1000, values[a][b] };
					list.Add(current);
				}
				obj.Data = list.ToArray();
				obj.Label = Enum.GetName(typeof (SnapshotValue), value);

				tObjects.Add(obj);
			}

			return tObjects.ToArray();
		}
		#endregion
	}
}

