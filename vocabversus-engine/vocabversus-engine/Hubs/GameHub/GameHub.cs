﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using vocabversus_engine.Hubs.GameHub.Responses;
using vocabversus_engine.Models;
using vocabversus_engine.Models.Exceptions;
using vocabversus_engine.Models.Responses;
using vocabversus_engine.Services;
using vocabversus_engine.Utility;

namespace vocabversus_engine.Hubs.GameHub
{
    public class GameHub : Hub
    {
        private readonly IGameInstanceCache _gameInstanceCache;
        private readonly IPlayerConnectionCache _playerConnectionCache;
        private readonly IGameEventService _gameEventService;
        public GameHub(IGameInstanceCache gameInstanceCache, IPlayerConnectionCache playerConnectionCache, IGameEventService gameEventService)
        {
            _gameInstanceCache = gameInstanceCache;
            _playerConnectionCache = playerConnectionCache;
            _gameEventService = gameEventService;
        }

        // When player connection goes out of scope, notify all relevant games
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            PlayerConnection? playerConnection = _playerConnectionCache.Retrieve(Context.ConnectionId);
            if (playerConnection is null) return;
            var gameInstance = _gameInstanceCache.Retrieve(playerConnection.GameInstanceIdentifier);
            if (gameInstance is null) return;
            gameInstance.PlayerInformation.DisconnectPlayer(playerConnection.PlayerIdentifier);
            await Clients.Group(playerConnection.GameInstanceIdentifier).SendAsync("UserLeft", Context.ConnectionId);
        }

        [HubMethodName("CheckGame")]
        public async Task<CheckGameInstanceResponse> CheckGameInstanceAvailability(string gameId)
        {
            // Get initialized game instance data if available
            var gameInstance = _gameInstanceCache.Retrieve(gameId) ?? throw GameHubException.CreateIdentifierError(gameId);
            return new CheckGameInstanceResponse
            {
                GameId = gameInstance.Identifier,
                GameState = gameInstance.State,
                PlayerCount = gameInstance.PlayerInformation.Players.Count,
                MaxPlayerCount = gameInstance.PlayerInformation.MaxPlayers
            };
        }

        [HubMethodName("Join")]
        public async Task<JoinGameInstanceResponse> JoinGameInstance(string gameId, string username)
        {
            // Get initialized game instance data, if no game instance was found either no game with given Id has been initialized or the session has expired
            var gameInstance = _gameInstanceCache.Retrieve(gameId) ?? throw GameHubException.CreateIdentifierError(gameId);
            var personalIdentifier = Context.ConnectionId;
            try
            {
                gameInstance.PlayerInformation.AddPlayer(personalIdentifier, username);
                
            }
            catch (PlayerException)
            {
                throw GameHubException.Create("Could not add user, either the game is full or user has already joined game instance", GameHubExceptionCode.UserAddFailed);
            }

            // subscribe player to the game instance via group connection
            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.OthersInGroup(gameId).SendAsync("UserJoined", username, personalIdentifier);

            // add connection instance to the connections cache for reference when the context goes out of scope (e.g. connection disconnects)
            _playerConnectionCache.Register(new PlayerConnection
            {
                Identifier = Context.ConnectionId,
                GameInstanceIdentifier = gameId,
                PlayerIdentifier = personalIdentifier
            }, Context.ConnectionId);

            return new JoinGameInstanceResponse
            {
                PersonalIdentifier = personalIdentifier,
                Players = gameInstance.PlayerInformation.Players
            };
        }

        [HubMethodName("Kick")]
        public async Task KickPlayerFromGameInstance(string gameId, string userIdentifier)
        {
            var gameInstance = _gameInstanceCache.Retrieve(gameId) ?? throw GameHubException.CreateIdentifierError(gameId);
            if (gameInstance.PlayerInformation.Players.FirstOrDefault(p => p.Key == userIdentifier).Value.isConnected) throw GameHubException.Create("Active players can not be kicked", GameHubExceptionCode.ActionNotAllowed);
            gameInstance.PlayerInformation.RemovePlayer(userIdentifier);
            await Clients.OthersInGroup(gameId).SendAsync("UserRemoved", userIdentifier);
        }

        [HubMethodName("Ready")]
        public async Task SetPlayerReadyState(string gameId, bool readyState)
        {
            var personalIdentifier = Context.ConnectionId;
            var gameInstance = _gameInstanceCache.Retrieve(gameId) ?? throw GameHubException.CreateIdentifierError(gameId);
            try
            {
                gameInstance.PlayerInformation.SetPlayerReadyState(personalIdentifier, readyState);
            }
            catch (GameInstanceException)
            {
                throw GameHubException.CreateIdentifierError(gameId);
            }
            catch (PlayerException)
            {
                throw GameHubException.Create("Failed to set user ready state", GameHubExceptionCode.UserEditFailed);
            }
            await Clients.OthersInGroup(gameId).SendAsync("UserReady", readyState, personalIdentifier);

            // If all active players are ready, start game
            if (gameInstance.PlayerInformation.Players.Where(p => p.Value.isConnected).All(p => p.Value.isReady))
            {
                gameInstance.State = GameState.Starting;
                var startTime = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds();
                await Clients.Group(gameId).SendAsync("GameStateChanged", GameState.Starting);
                await Clients.Group(gameId).SendAsync("GameStarting", startTime);
                await Task.Delay(Convert.ToInt32(startTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ContinueWith(async (_) =>
                {
                    gameInstance.State = GameState.Started;
                    await Clients.Group(gameId).SendAsync("GameStateChanged", GameState.Started);
                    GameRound gameRound = await _gameEventService.CreateGameRound(gameId, gameInstance.WordSet);
                    await Clients.Group(gameId).SendAsync("StartRound", new GameRoundResponse(gameRound));
                }).Unwrap();
            }
        }
    }
}
