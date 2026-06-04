using HarmonyLib;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using VRage.Render.Scene.Components;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace mleise.ProjectedLightsPlugin
{
	// Here we make the ambient light contribution of spot lights 8 times weaker to prevent our new projected lights
	// from just flooding everything with gray light.
	[HarmonyPatch]
	static class Patch_MyEnvironmentProbe_GatherLightAmbient
	{
		internal static MethodBase TargetMethod()
		{
			return AccessTools.Method("VRage.Render11.LightingStage.EnvironmentProbe.MyEnvironmentProbe:GatherLightAmbient");
		}

		internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var field = AccessTools.Field(typeof(Vector3), nameof(Vector3.Z));
			var matcher = new CodeMatcher(instructions);
			matcher.End().SearchBackwards(i => i.LoadsField(field)).SearchForward(i => i.opcode == OpCodes.Ldc_R4);

			matcher.SetOperandAndAdvance((float)matcher.Operand * 8f);

			return matcher.Opcode != OpCodes.Div
				? throw new InvalidOperationException("GatherLightAmbient was changed, the plugin needs to be updated.")
				: matcher.InstructionEnumeration();
		}
	}

	// Space Engineers can render up to 4 shadows. This patch contains heuristics to ensure the important spot lights are rendered first.
	[HarmonyPatch]
	static class Patch_MyLightsRendering_CullSpotLights
	{
		private static Type myLightsRenderingType = AccessTools.TypeByName("VRage.Render11.LightingStage.MyLightsRendering");
		private static FieldInfo resultsField = AccessTools.DeclaredField("VRage.Render11.Culling.MyCullQuery:Results");
		private static FieldInfo spotLightsField = AccessTools.DeclaredField("VRage.Render11.Culling.MyCullResults:SpotLights");
		private static FieldInfo viewerDistanceSquaredFastField = AccessTools.DeclaredField("VRage.Render11.Scene.Components.MyLightComponent:ViewerDistanceSquaredFast");
		private static FieldInfo outputListField = AccessTools.DeclaredField(myLightsRenderingType, "m_outputList");
		private static FieldInfo maxPointLightsField = AccessTools.DeclaredField(myLightsRenderingType, "m_maxPointLights");
		private static MethodBase sortFunction, removeRangeFunction;
		private static object sortComparer;

		internal static MethodBase TargetMethod()
		{
			return myLightsRenderingType.GetMethod("CullSpotLights", BindingFlags.Static | BindingFlags.NonPublic);
		}

		internal static void Prepare(MethodBase original)
		{
			if (original == null)
			{
				var methods = outputListField.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
				foreach (var method in methods)
				{
					if (method.Name == "Sort" && method.GetParameters().Length == 1)
					{
						sortFunction = method;
						break;
					}
				}
				sortComparer = AccessTools.DeclaredField("VRage.Render11.Scene.Components.MyLightComponent:SortComparer").GetValue(null);
				removeRangeFunction = outputListField.FieldType.GetMethod("RemoveRange", new Type[] { typeof(int), typeof(int) });
			}
		}

		internal static bool Prefix(object query)
		{
			var dataField = typeof(MyLightComponent).GetField("m_data", BindingFlags.Instance | BindingFlags.NonPublic);
			var originalDataField = typeof(MyLightComponent).GetField("m_originalData", BindingFlags.Instance | BindingFlags.NonPublic);

			// Sort spot lights by distance.
			var queryResults = resultsField.GetValue(query);
			var spotLights = (IList)spotLightsField.GetValue(queryResults);
			for (int i = spotLights.Count - 1; i >= 0; i--)
			{
				var myLightComponent = (MyLightComponent)spotLights[i];
				var distSq = myLightComponent.Owner.CalculateCameraDistanceSquaredFast();
				viewerDistanceSquaredFastField.SetValue(myLightComponent, distSq);

				var data = (UpdateRenderLightData)dataField.GetValue(myLightComponent);
				if (data.CastShadows && !data.Glare.Enabled)
				{
					// This is not a good way to detect if this light is handled by us, but we don't have access to the light logic here.
					// It will break for any spotlights that have no glare sprite.
					var originalData = (UpdateRenderLightData)originalDataField.GetValue(myLightComponent);
					if (originalData.CastShadows == distSq > originalData.Glare.QuerySize)
					{
						originalData.CastShadows = !originalData.CastShadows;
						originalDataField.SetValue(myLightComponent, originalData);
					}
				}
			}
			sortFunction.Invoke(spotLights, new object[] { sortComparer });

			// Remove any excess lights from the end of the list.
			var maxLights = (int)maxPointLightsField.GetValue(null);
			if (spotLights.Count > maxLights)
			{
				removeRangeFunction.Invoke(spotLights, new object[] { maxLights, spotLights.Count - maxLights });
			}
			return false;
		}
	}

	// This patch removes the 32 instances limit from the spotlight renderer.
	[HarmonyPatch]
	static class Patch_MyLightsRendering_RenderSpotlights
	{
		private static Type s_myLightsRenderingType = AccessTools.TypeByName("VRage.Render11.LightingStage.MyLightsRendering");

		internal static MethodBase TargetMethod()
		{
			return s_myLightsRenderingType.GetMethod("RenderSpotlights", BindingFlags.Static | BindingFlags.NonPublic);
		}

		internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var codes = (List<CodeInstruction>)instructions;
			for (var i = 0; i < codes.Count; i++)
			{
				if (codes[i].opcode == OpCodes.Ldc_I4_S && (sbyte)codes[i].operand == 32)
				{
					codes.RemoveAt(i + 2);
					break;
				}
			}
			return codes;
		}
	}

	// The render target texture used for emissive materials is 8-bit, so the amount of bloom we get is hard capped by the
	// renderers emissive bloom multiplier. In order to get a realistically blinding light without resorting to the glare
	// sprites we need to bump up this bloom response a lot.
	[HarmonyPatch(typeof(MyPostprocessSettings), nameof(MyPostprocessSettings.GetProcessedData))]
	static class Patch_MyPostprocessSettings_GetProcessedData
	{
		internal static void Postfix(ref MyPostprocessSettings.Layout __result)
		{
			__result.BloomEmissiveness *= LightDefinition.EMISSIVE_BOOST;
		}
	}

	//Replace references to DefaultEmissivity
	[HarmonyPatch]
	static class Patch_MyModelProperties_DefaultEmissivity
	{
		internal static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
		{
			AccessTools.Constructor(AccessTools.TypeByName("VRage.Render11.Scene.Components.MyModelProperties")),
			AccessTools.Method("VRage.Render11.Scene.Components.MyRenderableComponent:CreateRenderableProxyForPart"),
			AccessTools.Method("VRage.Render11.Scene.Components.MyRenderableComponent:RebuildRenderProxies"),
			AccessTools.Method("VRageRender.MyVoxelCellComponent:CreateRenderableProxyForPart"),
        };
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
			var field = AccessTools.Field("VRage.Render11.Scene.Components.MyModelProperties:DefaultEmissivity");

            foreach (var instruction in instructions)
			{
				if (instruction.LoadsField(field))
				{
					instruction.opcode = OpCodes.Ldc_R4;
                    instruction.operand = LightDefinition.EMISSIVE_BOOST_INV;
                }

                yield return instruction;
            }

        }
    }


    // Now since everything blooms out more than desired, we have to write smaller values into the emissive render target.
    [HarmonyPatch]
	static class Patch_MyRender11_ProcessMessageInternal
	{
		internal static void Prepare(MethodBase original)
		{
			if (original == null)
			{
				var myInstanceMaterialType = AccessTools.TypeByName("VRage.Render11.GeometryStage2.Instancing.MyInstanceMaterial");
				var defaultField = AccessTools.Field(myInstanceMaterialType, "Default");
				var material = defaultField.GetValue(null);
				AccessTools.PropertySetter(myInstanceMaterialType, "Emissivity").Invoke(material, new object[] { LightDefinition.EMISSIVE_BOOST_INV });
				defaultField.SetValue(null, material);
			}
		}

		internal static MethodBase TargetMethod() => AccessTools.Method("VRageRender.MyRender11:ProcessMessageInternal");

		internal static void Prefix(MyRenderMessageBase message)
		{
			switch (message.MessageType)
			{
				case MyRenderMessageEnum.UpdateColorEmissivity: // Used for example by batteries, spammed by H2/O2 generators.
					((MyRenderMessageUpdateColorEmissivity)message).Emissivity *= LightDefinition.EMISSIVE_BOOST_INV;
					break;
				case MyRenderMessageEnum.UpdateModelProperties: // Used when lights change their emissive texture.
					((MyRenderMessageUpdateModelProperties)message).Emissivity *= LightDefinition.EMISSIVE_BOOST_INV;
					break;
			}
		}
	}

	// The light bulb color (e.g. the emissive base color) was originally calculated in a way that starts at a dark
	// gray and adds the light color set in the terminal in a scaled down version. We changed it to directly use the
	// terminal color and convert it from sRGB to linear RGB, so it becomes pretty much WYSIWYG. Without the conversion
	// to linear RGB, small color values had a tendency to become overly intense, turning orange into yellow and teal
	// blue into turqoise for example.
	[HarmonyPatch]
	static class Patch_MyInstanceMaterial_ColorMult_set
	{
		internal static MethodBase TargetMethod()
		{
			return AccessTools.PropertySetter("VRage.Render11.GeometryStage2.Instancing.MyInstanceMaterial:ColorMult");
		}

		internal static void Prefix(ref Vector3 value)
		{
			value = ColorExtensions.ToLinearRGB(value);
		}
	}

	// This isn't the complete story though. Emissive materials are also rendered into the environment reflection map
	// where they cause vending machines or lights to be mirrored on the opposite wall. Unfortunately the color
	// components therein are clamped via pixel shader to an intensity of 1000, resulting in a 2nd source of "my orange
	// just turned yellow" when rendering very bright emissive materials.
	//
	// PreprocessShader.Postfix and ShaderBytecode.Compile.Prefix below cooperate to lift that clamp to 3000, but they
	// DO NOT share mutable state:
	//   - Postfix rewrites the preprocessed shader text that Space Engineers uses to build the shader-cache hash.
	//     Changing that text alone is enough to make the cache miss on first run so the real Compile path is taken.
	//   - Prefix independently identifies the same target shader from (sourceFileName, defines), re-runs the D3D
	//     preprocessor, applies the same replacement, and hands the substituted source to D3DCompile.
	// Self-contained patches mean cache hits, JIT inlining of either method, and unrelated plugins compiling their
	// own shaders through this overload are all non-issues.
	static class EnvReflectionPixelPatch
	{
		internal const string OldClamp = @"    return float4 ( clamp ( shaded , 0 , 1000 ) , 1 ) ; ";
		internal const string NewClamp = @"    return float4 ( clamp ( shaded , 0 , 3000 ) , 1 ) ; ";

		// True iff (filepath, macros) point at the forward-renderer pixel shader for the env reflection map.
		// Both patches identify the target this way; the helper is the only thing they share, and it's a pure
		// function over its arguments.
		internal static bool IsTarget(string filepath, ShaderMacro[] macros)
		{
			if (filepath == null || macros == null || !filepath.EndsWith(@"\Pixel.hlsl"))
				return false;
			foreach (var define in macros)
			{
				if (define.Name == "RENDERING_PASS")
					// Pass 2 is the forward renderer for the environment reflection map - the one we want to override.
					return define.Definition == "2";
			}
			return false;
		}
	}

	// This patch intercepts and modifies the preprocessed shader text that is used to build a hash to look up the
	// compiled shader in the cache. Space Engineers will not find the modified shader in the cache yet and compile it.
	[HarmonyPatch(typeof(MyShaderCompiler), "PreprocessShader")]
	static class Patch_MyShaderCompiler_PreprocessShader
	{
		internal static void Postfix(string filepath, ShaderMacro[] macros, ref string __result)
		{
			// PreprocessShader returns null when SE's internal preprocess fails; don't NRE on its quiet error path.
			if (__result == null || !EnvReflectionPixelPatch.IsTarget(filepath, macros)) return;
			__result = __result.Replace(EnvReflectionPixelPatch.OldClamp, EnvReflectionPixelPatch.NewClamp);
		}
	}

	// Here we substitute the modified shader source on the fly. We identify the target ourselves from the Compile
	// arguments and re-run preprocessing, so this patch is independent of the PreprocessShader patch above.
	[HarmonyPatch(typeof(ShaderBytecode), nameof(ShaderBytecode.Compile), new Type[] { typeof(string), typeof(string), typeof(string), typeof(ShaderFlags), typeof(EffectFlags), typeof(ShaderMacro[]), typeof(Include), typeof(string), typeof(SecondaryDataFlags), typeof(DataStream) })]
	static class Patch_ShaderBytecode_Compile
	{
		internal static void Prefix(ref string shaderSource, ref ShaderFlags shaderFlags,
		                            string sourceFileName, ShaderMacro[] defines, Include include)
		{
			// Other plugins' Compile calls reach this Prefix too but fail IsTarget (different filepath, or no
			// RENDERING_PASS=2) and pass through untouched.
			if (!EnvReflectionPixelPatch.IsTarget(sourceFileName, defines)) return;
			shaderSource = ShaderBytecode.Preprocess(shaderSource, defines, include, sourceFileName)
			                             .Replace(EnvReflectionPixelPatch.OldClamp, EnvReflectionPixelPatch.NewClamp);
			shaderFlags = ShaderFlags.OptimizationLevel3;
		}
	}
}