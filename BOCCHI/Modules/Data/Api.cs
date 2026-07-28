using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BOCCHI.Modules.ForkedTower;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Data;

public class Api(DataModule module)
{
    private ForkedTowerModule forkedTowerModule = null!;

    private readonly HttpClient client = new();

    private readonly EnemyDataHelper EnemyData = new();

    private readonly TrapDataHelper TrapData = new();

    public void Initialize()
    {
        forkedTowerModule = module.GetModule<ForkedTowerModule>();
    }

    public async Task SendEnemyData(IGameObject obj)
    {
        var enemy = new Enemy(obj);
        if (EnemyData.HasSharedData(enemy))
        {
            return;
        }

        const string url = "https://api.oc.ohkannaduh.com/monster_spawn";
        var payload = MonsterPayload.Create(enemy);
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("x-api-key", "b1fba45b-6554-4dec-b53d-073deb8e3869");

        try
        {
            var response = await client.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                DebugLog.Info("Data sent successfully.");
                var responseBody = await response.Content.ReadAsStringAsync();
                EnemyData.MarkSharedData(enemy);
                DebugLog.Info($"Response: {responseBody}");
            }
            else
            {
                DebugLog.Debug($"Failed to send data. Status code: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                DebugLog.Debug($"Error response: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Debug($"Failed to send request: {ex.Message}");
        }
    }


    public async Task SendTrapData(IGameObject obj)
    {
        var trap = new Trap(obj, forkedTowerModule.TowerRun.Hash);
        if (TrapData.HasSharedData(trap))
        {
            return;
        }

        const string url = "https://api.oc.ohkannaduh.com/trap_position";
        var payload = TrapPayload.Create(trap);
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");


        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("x-api-key", "b1fba45b-6554-4dec-b53d-073deb8e3869");

        try
        {
            var response = await client.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                DebugLog.Info("Data sent successfully.");
                var responseBody = await response.Content.ReadAsStringAsync();
                TrapData.MarkSharedData(trap);
                DebugLog.Info($"Response: {responseBody}");
            }
            else
            {
                DebugLog.Debug($"Failed to send data. Status code: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                DebugLog.Debug($"Error response: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Debug($"Failed to send request: {ex.Message}");
        }
    }
}
