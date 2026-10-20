using System;
using System.Reflection;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's switch (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 4.1, 6.1).
// BotCpuScript holds the bot_cpu_meter server property, which HEARTHDAOC_BOT_CPU sets at every start (4.2) and the
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are.
[TestFixture, NonParallelizable]
public sealed class UT_BotCpuScript
{
    [Test]
    public void TheSwitchIsAPublicStaticServerPropertyOffInCode()
    {
        // The server finds a property only on a public static field (ServerProperties.AllDomainProperties reads
        // GetFields(), then IsStatic). HearthDAoC turns it on through HEARTHDAOC_BOT_CPU; the description must equal
        // deploy/bin/server_properties.py's CPU_DESCRIPTION (deploy/tests/test_bot_cpu.py).
        FieldInfo field = Array.Find(typeof(BotCpuScript).GetFields(), f => f.Name == "BOT_CPU_METER");
        Assert.That(field, Is.Not.Null);
        Assert.That(field.IsStatic, Is.True);
        Assert.That(field.FieldType, Is.EqualTo(typeof(bool)));
        ServerPropertyAttribute property = field.GetCustomAttribute<ServerPropertyAttribute>();
        Assert.That(property, Is.Not.Null);
        Assert.That(property.Category, Is.EqualTo("server"));
        Assert.That(property.Key, Is.EqualTo("bot_cpu_meter"));
        Assert.That(property.DefaultValue, Is.TypeOf<bool>().And.EqualTo(false));
        Assert.That(property.Description, Is.EqualTo(
            "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log " +
            "every minute; a GM's /botcpu turns it on or off until the next start"));
    }
}
