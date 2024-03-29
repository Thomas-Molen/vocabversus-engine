﻿using vocabversus_engine.Models;

namespace vocabversus_engine.Hubs.GameHub.Responses
{
    public class JoinGameInstanceResponse
    {
        public Dictionary<string, PlayerRecord> Players { get; set; } = new();
        public List<GameRoundResponse> Rounds { get; set; } = new();
    }
}
