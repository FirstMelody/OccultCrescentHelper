using System;
using ECommons.DalamudServices;

namespace BOCCHI;

public static class DebugLog
{
    private static Config? config;

    public static bool Enabled
    {
        get => config?.DebugLoggingEnabled == true;
    }

    public static void Initialize(Config pluginConfig)
    {
        config = pluginConfig;
    }

    public static void Chat(string message)
    {
        if (Enabled)
        {
            Svc.Chat.Print(message);
        }
    }

    public static void Info(string message)
    {
        if (Enabled)
        {
            Svc.Log.Info(message);
        }
    }

    public static void Debug(string message)
    {
        if (Enabled)
        {
            Svc.Log.Debug(message);
        }
    }

    public static void Information(string messageTemplate, params object[] values)
    {
        if (Enabled)
        {
            Svc.Log.Information(messageTemplate, values);
        }
    }

    public static void Warning(string message)
    {
        if (Enabled)
        {
            Svc.Log.Warning(message);
        }
    }

    public static void Warning(string messageTemplate, params object[] values)
    {
        if (Enabled)
        {
            Svc.Log.Warning(messageTemplate, values);
        }
    }

    public static void Warning(Exception exception, string message)
    {
        if (Enabled)
        {
            Svc.Log.Warning(exception, message);
        }
    }
}
