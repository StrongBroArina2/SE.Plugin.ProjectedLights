using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using VRage.Plugins;
using VRage.Utils;

namespace mleise.ProjectedLightsPlugin
{
    sealed class Main : IPlugin
    {
      public void Init(object gameInstance)
        {
            // Ensure that we aren't loading twice. Not anymore pal

            // Patch Space Engineers.
            Harmony harmony = new Harmony(typeof(Main).Namespace);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Space Engineers compiles a couple of shaders before plugins have a chance to intercept them.
            // By triggering a recompile, we can go back and patch them. They will eventually all be cached in
            // "%APPDATA%\SpaceEngineers\ShaderCache2".
            AccessTools.Method("VRageRender.MyPixelShaders:Recompile").Invoke(null, null);
        }

        /// <summary>Called when the game is closed.</summary>
        public void Dispose() { }

        /// <summary>Called at the end of each game update at 60 Hz, with only audio trailing it.</summary>
        public void Update() { }
    }
}