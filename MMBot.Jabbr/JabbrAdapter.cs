﻿using System;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using Common.Logging;
using Microsoft.AspNet.SignalR.Client;
using MMBot.Jabbr.JabbrClient;

namespace MMBot.Jabbr
{
    public class JabbrAdapter : Adapter
    {
        private JabbRClient _client;

        // TODO: Move to environment variables / config
        private string _host;
        private string _nick;
        private string _password;
        private string[] _rooms;
        private bool _isConfigured = false;
        

        private void Configure()
        {
            _host = Robot.GetConfigVariable("MMBOT_JABBR_HOST");
            _nick = Robot.GetConfigVariable("MMBOT_JABBR_NICK");
            _password = Robot.GetConfigVariable("MMBOT_JABBR_PASSWORD");
            _rooms = (Robot.GetConfigVariable("MMBOT_JABBR_ROOMS") ?? string.Empty)
                .Trim()
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            _isConfigured = _host != null;
        }

        public JabbrAdapter(Robot robot, ILog logger, string adapterId)
            : base(robot, logger, adapterId)
        {
            Configure();
        }

        private void SetupJabbrClient()
        {
            if (_client != null)
            {
                return;
            }

            _client = new JabbRClient(_host)
            {
                AutoReconnect = true
            };

            _client.MessageReceived += ClientOnMessageReceived;

            _client.UserJoined += OnUserJoined;

            _client.UserLeft += OnUserLeft;

            _client.PrivateMessage += OnPrivateMessage;
            
        }

        private void OnPrivateMessage(string @from, string to, string message)
        {
            Logger.Info(string.Format("*PRIVATE* {0} -> {1} ", @from, message));

            var user = new User(@from, @from, new string[0], null, Id);

            if (user.Name != _nick)
            {
                Task.Run(() =>
                Robot.Receive(new TextMessage(user, string.Format("{0} {1}", to, message), null)));
            }
        }

        private void OnUserLeft(User user, string room)
        {
            Logger.Info(string.Format("{0} left {1}", user.Name, room));
        }

        private void OnUserJoined(User user, string room, bool isOwner)
        {
            Logger.Info(string.Format("{0} joined {1}", user.Name, room));
        }

        private void OnClientStateChanged(StateChange state)
        {
            if (state.NewState == ConnectionState.Disconnected)
            {
                Logger.Warn("Jabbr client is disconnected");
            }
            else
            {
                Logger.Info(string.Format("Jabbr client is {0}", state.NewState));
            }
        }

        private void ClientOnMessageReceived(JabbrClient.Models.Message message, string room)
        {
            Logger.Info(string.Format("[{0}] {1}: {2}", message.When, message.User.Name, message.Content));

            // TODO: implement user lookup
            //user = self.robot.brain.userForName msg.name
            //unless user?
            //    id = (new Date().getTime() / 1000).toString().replace('.','')
            //    user = self.robot.brain.userForId id
            //    user.name = msg.name

            var user = new User(message.User.Name, message.User.Name, new string[0], room, Id);

            //TODO: Filter out messages from mmbot itself using the current nick
            if(user.Name != _nick)
            {
                Task.Run(() => 
                Robot.Receive(new TextMessage(user, message.Content, message.Id)));
            }
        }

        public override async Task Run()
        {
            if (!_isConfigured)
            {
                throw new AdapterNotConfiguredException();
            }
            Logger.Info(string.Format("Logging into JabbR..."));

            SetupJabbrClient();

            var result = await _client.Connect(_nick, _password);

            _client.StateChanged += OnClientStateChanged;

            Logger.Info(string.Format("Logged on successfully. {0} is currently in the following rooms:", _nick));
            foreach (var room in result.Rooms)
            {
                Logger.Info(string.Format(" - " + room.Name + (room.Private ? " (private)" : string.Empty)));
                Rooms.Add(room.Name);
            }

            foreach (var room in _rooms.Where(room => !result.Rooms.Select(r => r.Name).Contains(room)))
            {
                try
                {
                    await _client.JoinRoom(room);
                    Rooms.Add(room);
                    Logger.Info(string.Format("Successfully joined room {0}", room));
                }
                catch (Exception e)
                {
                    Logger.Info(string.Format("Could not join room {0}: {1}", room, e.Message));
                }
            }
        }

        public override Task Close()
        {
            _client.Disconnect();
            _client.MessageReceived -= ClientOnMessageReceived;
            _client.UserJoined -= OnUserJoined;
            _client.UserLeft -= OnUserLeft;
            _client.PrivateMessage -= OnPrivateMessage;
            return TaskAsyncHelper.Empty;
        }

        public override async Task Topic(Envelope envelope, params string[] messages)
        {
            await base.Topic(envelope, messages);
            
            if (envelope != null && envelope.Room != null)
            {
                var message = string.Join(" ", messages);
                await _client.Send(string.Format("/topic {0}", message.Substring(0, Math.Min(80, message.Length))), envelope.Room);
            }
        }

        public override async Task Topic(string roomName, params string[] messages)
        {
            await base.Topic(roomName, messages);

            var room = await _client.GetRoomInfo(roomName);
            if(room != null)
            {
                var message = string.Join(" ", messages);
                await _client.Send(string.Format("/topic {0}", message.Substring(0, Math.Min(80, message.Length))), room.Name);
            }
        }

        public override async Task Send(Envelope envelope, params string[] messages)
        {
            await base.Send(envelope, messages);

            if (messages == null)
            {
                return;
            }

            foreach (var message in messages.Where(message => !string.IsNullOrWhiteSpace(message)))
            {
                if (!string.IsNullOrEmpty(envelope.User.Room))
                {
                    await _client.Send(message, envelope.User.Room);
                }
                else
                {
                    await _client.SendPrivateMessage(envelope.User.Name, message);
                }
            }
        }

        public override async Task Reply(Envelope envelope, params string[] messages)
        {
            await base.Reply(envelope, messages);

            foreach (var message in messages.Where(message => !string.IsNullOrWhiteSpace(message)))
            {
                await _client.Send(string.Format("@{0} {1}", envelope.User.Name, message), envelope.User.Room);
            }
        }
    }
}
