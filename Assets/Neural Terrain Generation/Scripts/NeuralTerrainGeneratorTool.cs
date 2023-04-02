#if (UNITY_EDITOR)

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Barracuda;
using UnityEditor;
using UnityEditor.TerrainTools;
using UnityEngine.TerrainTools;
using NeuralTerrainGeneration;

namespace NeuralTerrainGeneration
{
    public class NeuralTerrainGeneratorTool : TerrainPaintTool<NeuralTerrainGeneratorTool>
    {
        // General.
        private TensorMathHelper tensorMathHelper = new TensorMathHelper();
        private TerrainHelper terrainHelper = new TerrainHelper();
        private int modelOutputWidth = 256;
        private int modelOutputHeight = 256;
        private int upSampledWidth = 0;
        private int upSampledHeight = 0;
        private float heightMultiplier = 0.5f;
        private int channels = 1;
        private NNModel modelAsset;
        private Model runtimeModel;

        // Blending.
        private float radius1 = 128.0f;
        private float radius2 = 256.0f;
        private float bValue = 2.5f;
        private bool keepNeighborHeights = false;
        private NeighborBlender neighborBlender = new NeighborBlender();
        
        // Diffusion.
        private const float maxSignalRate = 0.9f;
        private const float minSignalRate = 0.02f;
        private Diffuser diffuser = new Diffuser();
        private int fromScratchDiffusionSteps = 20;
        private int fromSelectedDiffusionSteps = 20;
        private int fromSelectedStartingStep = 18;
        private float selectedTerrainWeight = 0.55f;
        private int brushHeightmapDiffusionSteps = 20;

        // Upsampling.
        private enum UpSampleMode {Bicubic};
        private UpSampleMode upSampleMode = UpSampleMode.Bicubic;
        // Left: upsample resolution, right: upsample factor.
        private enum UpSampleResolution 
        {
            _256 = 1, 
            _512 = 2, 
            _1024 = 4, 
            _2048 = 8, 
            _4096 = 16
        };
        private UpSampleResolution upSampleResolution = UpSampleResolution._512;
        private BicbubicUpSampler bicubicUpSampler = new BicbubicUpSampler();
        
        // Downsampling.
        private DownSampler downSampler = new DownSampler();

        // Brushes.
        private bool brushesEnabled = false;
        private Texture2D brushTexture1;
        private Texture2D brushTexture2;
        private float brushOpacity = 0.1f;
        private float brushSize = 25f;
        private float brushRotation = 0f;
        private Texture2D brushMask;
        private Texture2D brushHeightmap;
        private Texture2D brushHeightmapMasked;

        public override string GetName()
        {
            return "Neural Terrain Generator";
        }

        public override string GetDescription()
        {
            return "Diffusion based neural terrain generator.";
        }

        private void GenerateMaskedBrushHeightmap()
        {
            if(brushHeightmap == null || brushMask == null) { return; }

            brushHeightmapMasked = new Texture2D(brushHeightmap.width, brushHeightmap.height);

            Color[] brushHeightmapColors = brushHeightmap.GetPixels();
            Color[] brushMaskColors = brushMask.GetPixels();
            Color[] brushHeightmapMaskedColors = new Color[brushHeightmapColors.Length];
            
            for(int i = 0; i < brushHeightmapColors.Length; i++)
            {
                brushHeightmapMaskedColors[i] = brushHeightmapColors[i] * brushMaskColors[i].r;
            }

            brushHeightmapMasked.SetPixels(brushHeightmapMaskedColors);
            brushHeightmapMasked.Apply();
        }

        public override void OnInspectorGUI(Terrain terrain, IOnInspectorGUI editContext)
        {
            modelAsset = (NNModel)EditorGUILayout.ObjectField("Model Asset", modelAsset, typeof(NNModel), false);

            EditorGUILayout.Space();
            EditorGUILayout.Space();
            BrushGUI();
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            FromScratchGUI(terrain);
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            FromSelectedGUI(terrain);
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            BlendGUI(terrain);
        }

        private void BrushGUI()
        {
            if(!brushesEnabled)
            {
                if(GUILayout.Button("Enable Brush"))
                {
                    brushesEnabled = true;
                }
            }
            else
            {
                if(GUILayout.Button("Disable Brush"))
                {
                    brushesEnabled = false;
                }

                // Brush mask.
                EditorGUILayout.HelpBox("Brush masks must be 256x256.", MessageType.Info);
                Texture2D tempBrushMask = (Texture2D)EditorGUILayout.ObjectField("Brush Mask", brushMask, typeof(Texture2D), false);
                if(tempBrushMask)
                {
                    brushMask = tempBrushMask;
                    GenerateMaskedBrushHeightmap();
                }
                EditorGUILayout.Space();

                // Brush controls.
                brushOpacity = EditorGUILayout.Slider("Opacity", brushOpacity, 0, 1);
                brushSize = EditorGUILayout.Slider("Size", brushSize, .001f, 2000f);
                brushRotation = EditorGUILayout.Slider("Rotation", brushRotation, 0, 360);

                EditorGUILayout.Space();

                // Diffusion controls.
                brushHeightmapDiffusionSteps = EditorGUILayout.IntField("Brush Diffusion Steps", brushHeightmapDiffusionSteps);

                if(GUILayout.Button("Generate Brush Heighmap"))
                {
                    // Brush heightmap is not upsampled, so keep it at 256x256.
                    float[] brushHeightmapArray = GenerateHeightmap(UpSampleResolution._256, brushHeightmapDiffusionSteps);

                    Color[] colorBrushHeightmap = new Color[brushHeightmapArray.Length];
                    for(int i = 0; i < brushHeightmapArray.Length; i++)
                    {
                        colorBrushHeightmap[i] = new Color(brushHeightmapArray[i], brushHeightmapArray[i], brushHeightmapArray[i]);
                    }

                    // Dimensions equal to model output dimensions because there is no upsampling.
                    brushHeightmap = new Texture2D(modelOutputWidth, modelOutputHeight);
                    brushHeightmap.SetPixels(0, 0, modelOutputWidth, modelOutputHeight, colorBrushHeightmap);
                    brushHeightmap.Apply();
                }

                // Display brush heightmap and masked heightmap if they exist.
                if(brushHeightmap != null)
                {
                    EditorGUILayout.LabelField("Brush Heightmap:");
                    GUILayout.Box(brushHeightmap);
                }
                if(brushHeightmapMasked != null)
                {
                    EditorGUILayout.LabelField("Masked Brush Heightmap:");
                    GUILayout.Box(brushHeightmapMasked);
                }
            }
        }

        private void FromScratchGUI(Terrain terrain)
        {
            heightMultiplier = EditorGUILayout.FloatField("Height Multiplier", heightMultiplier);
            upSampleMode = (UpSampleMode)EditorGUILayout.EnumPopup("UpSample Mode", upSampleMode);
            upSampleResolution = (UpSampleResolution)EditorGUILayout.EnumPopup("UpSample Resolution", upSampleResolution);
            CalculateUpSampledDimensions();
            CalculateBlendingRadii();

            fromScratchDiffusionSteps = EditorGUILayout.IntField("From Scratch Diffusion Steps", fromScratchDiffusionSteps);

            if(GUILayout.Button("Generate Terrain From Scratch"))
            {
                float[] heightmap = GenerateHeightmap(upSampleResolution, fromScratchDiffusionSteps);
                terrainHelper.SetTerrainHeights(terrain, heightmap, upSampledWidth, upSampledHeight, heightMultiplier);
            }
        }

        private void FromSelectedGUI(Terrain terrain)
        {
            fromSelectedDiffusionSteps = EditorGUILayout.IntField("From Selected Diffusion Steps", fromSelectedDiffusionSteps);
            fromSelectedStartingStep = EditorGUILayout.IntField("From Selected Starting Step", fromSelectedStartingStep);
            selectedTerrainWeight = EditorGUILayout.Slider("Selected Terrain Weight", selectedTerrainWeight, 0.0f, 1.0f);

            if(GUILayout.Button("Generate Terrain From Selected"))
            {
                int terrainResolution = terrain.terrainData.heightmapResolution;
                int downSampleFactor = 1;
                bool validTerrainResolution = true;
                switch(terrainResolution)
                {
                    case 257:
                        downSampleFactor = 1;
                        break;
                    case 513:
                        downSampleFactor = 2;
                        break;
                    case 1025:
                        downSampleFactor = 4;
                        break;
                    case 2049:
                        downSampleFactor = 8;
                        break;
                    case 4097:
                        downSampleFactor = 16;
                        break;
                    default:
                        Debug.LogError("Selected terrain resolution must be one of the following: 257, 513, 1025, 2049, or 4097.");
                        validTerrainResolution = false;
                        break;
                }

                if(validTerrainResolution)
                {
                    float[,] heightmapArray = terrain.terrainData.GetHeights(0, 0, terrainResolution, terrainResolution);
                    Tensor heightmap = tensorMathHelper.TwoDimensionalArrayToTensor(heightmapArray);
                    Tensor downSampledHeightmap = downSampler.DownSample(heightmap, downSampleFactor);
                    Tensor scaledHeightmap = tensorMathHelper.ScaleTensor(downSampledHeightmap, selectedTerrainWeight);
                    
                    Tensor noise = tensorMathHelper.RandomNormalTensor(1, modelOutputHeight, modelOutputWidth, 1);
                    Tensor scaledNoise = tensorMathHelper.ScaleTensor(noise, 1.0f - selectedTerrainWeight);

                    Tensor customInput = tensorMathHelper.AddTensor(scaledHeightmap, scaledNoise);

                    float[] newHeightmap = GenerateHeightmap(
                        upSampleResolution, 
                        fromSelectedDiffusionSteps, 
                        fromSelectedStartingStep, 
                        customInput
                    );

                    terrainHelper.SetTerrainHeights(terrain, newHeightmap, upSampledWidth, upSampledHeight, heightMultiplier);

                    heightmap.Dispose();
                    downSampledHeightmap.Dispose();
                    scaledHeightmap.Dispose();
                    noise.Dispose();
                    scaledNoise.Dispose();
                    customInput.Dispose();
                }
            }
        }

        private void BlendGUI(Terrain terrain)
        {
            bValue = EditorGUILayout.Slider("Blend Function Start Value", bValue, 2.5f, 5.0f);
            //keepNeighborHeights = EditorGUILayout.Toggle("Keep Neighbor Heights", keepNeighborHeights);

            if(GUILayout.Button("Blend With Neighbors"))
            {
                neighborBlender.BlendAllNeighbors(
                    terrain, 
                    upSampledWidth, 
                    upSampledHeight, 
                    radius1, 
                    radius2, 
                    bValue, 
                    keepNeighborHeights
                );
            } 
        }

        private void RenderIntoPaintContext(PaintContext paintContext, Texture brushTexture, BrushTransform brushXform)
        {
            // Get the built-in painting Material reference.
            Material mat = TerrainPaintUtility.GetBuiltinPaintMaterial();
            
            // Bind the current brush texture.
            mat.SetTexture("_BrushTex", brushTexture);

            // Bind the tool-specific shader properties.
            var opacity = Event.current.control ? -brushOpacity : brushOpacity;
            mat.SetVector("_BrushParams", new Vector4(opacity, 0.0f, 0.0f, 0.0f));

            // Setup the material for reading from/writing into the PaintContext texture data. 
            // This is a necessary step to setup the correct shader properties for 
            // appropriately transforming UVs and sampling textures within the shader.
            TerrainPaintUtility.SetupTerrainToolMaterialProperties(paintContext, brushXform, mat);
            
            // Render into the PaintContext's destinationRenderTexture using 
            // the built-in painting Material - the id for the Raise/Lower pass is 0.
            Graphics.Blit(paintContext.sourceRenderTexture, paintContext.destinationRenderTexture, mat, 0);
        }

        // Render Tool previews in the SceneView
        public override void OnRenderBrushPreview(Terrain terrain, IOnSceneGUI editContext)
        {
            // Don't do anything if brushes are disabled.
            if(!brushesEnabled) { return; }

            // Dont render preview if this isnt a Repaint.
            if(Event.current.type != EventType.Repaint) { return; }

            // Only do the rest if user mouse hits valid terrain.
            if(!editContext.hitValidTerrain) { return; }

            // Get the current BrushTransform under the mouse position relative to the Terrain.
            BrushTransform brushXform = TerrainPaintUtility.CalculateBrushTransform(
                terrain, 
                editContext.raycastHit.textureCoord, 
                brushSize, 
                brushRotation
            );

            // Get the PaintContext for the current BrushTransform. 
            // This has a sourceRenderTexture from which to read existing Terrain texture data.
            PaintContext paintContext = TerrainPaintUtility.BeginPaintHeightmap(
                terrain, 
                brushXform.GetBrushXYBounds(), 
                1
            );

            // Get the built-in Material for rendering Brush Previews
            Material previewMaterial = TerrainPaintUtilityEditor.GetDefaultBrushPreviewMaterial();

            // Render the brush preview for the sourceRenderTexture. 
            // This will show up as a projected brush mesh rendered on top of the Terrain
            TerrainPaintUtilityEditor.DrawBrushPreview(
                paintContext, 
                TerrainBrushPreviewMode.SourceRenderTexture, 
                brushHeightmapMasked, 
                brushXform, 
                previewMaterial, 
                0
            );

            // Render changes into the PaintContext destinationRenderTexture
            RenderIntoPaintContext(paintContext, brushHeightmapMasked, brushXform);

            // Restore old render target.
            RenderTexture.active = paintContext.oldRenderTexture;

            // Bind the sourceRenderTexture to the preview Material. This is used to compute deltas in height
            previewMaterial.SetTexture("_HeightmapOrig", paintContext.sourceRenderTexture);

            // Render a procedural mesh displaying the delta/displacement in height from the source Terrain texture data. 
            // When modifying Terrain height, this shows how much the next paint operation will alter the Terrain height.
            TerrainPaintUtilityEditor.DrawBrushPreview(
                paintContext, 
                TerrainBrushPreviewMode.DestinationRenderTexture, 
                brushHeightmapMasked, 
                brushXform, 
                previewMaterial, 
                1
            );

            // Cleanup resources
            TerrainPaintUtility.ReleaseContextResources(paintContext);
        }

        // Perform painting operations that modify the Terrain texture data.
        public override bool OnPaint(Terrain terrain, IOnPaint editContext)
        {
            if(!brushesEnabled) { return false; }

            // Get the current BrushTransform under the mouse position relative to the Terrain
            BrushTransform brushXform = TerrainPaintUtility.CalculateBrushTransform(
                terrain, 
                editContext.uv, 
                brushSize, 
                brushRotation
            );

            // Get the PaintContext for the current BrushTransform. 
            // This has a sourceRenderTexture from which to read existing Terrain texture data
            // and a destinationRenderTexture into which to write new Terrain texture data
            PaintContext paintContext = TerrainPaintUtility.BeginPaintHeightmap(
                terrain, 
                brushXform.GetBrushXYBounds()
            );

            // Call the common rendering function used by OnRenderBrushPreview and OnPaint
            RenderIntoPaintContext(paintContext, brushHeightmapMasked, brushXform);

            // Commit the modified PaintContext with a provided string for tracking Undo operations. 
            // This function handles Undo and resource cleanup for you.
            TerrainPaintUtility.EndPaintHeightmap(paintContext, "Terrain Paint - Raise or Lower Height");

            // Return whether or not Trees and Details should be hidden while painting with this Terrain Tool
            return true;
        }

        private void CalculateUpSampledDimensions()
        {
            int upSampleFactor = (int)upSampleResolution;
            upSampledWidth = modelOutputWidth * upSampleFactor;
            upSampledHeight = modelOutputHeight * upSampleFactor;
        }

        private void CalculateBlendingRadii()
        {
            radius1 = upSampledWidth / 2;
            radius2 = upSampledWidth;
        }

        private float[] GenerateHeightmap(
            UpSampleResolution upSampleResolutionArg, 
            int diffusionSteps, 
            int startingStep = 0, 
            Tensor customInput = null
        )
        {
            if(runtimeModel == null)
            {
                if(modelAsset != null)
                {
                    runtimeModel = ModelLoader.Load(modelAsset);
                }
                else
                {
                    Debug.LogError("Model asset is null.");
                    return null;
                }
            }

            float[] heightmap = new float[upSampledWidth * upSampledHeight];
            using(var worker = WorkerFactory.CreateWorker(WorkerFactory.Type.ComputePrecompiled, runtimeModel))
            {
                Tensor input = new Tensor(1, modelOutputWidth, modelOutputHeight, channels);
                
                if(customInput != null)
                {
                    input = customInput;
                }
                else
                {
                    input = tensorMathHelper.RandomNormalTensor(
                        1, 
                        modelOutputWidth, 
                        modelOutputHeight, 
                        channels
                    );
                }

                Tensor reverseDiffusionOutput = diffuser.ReverseDiffusion(
                    worker, 
                    input, 
                    modelOutputWidth, 
                    modelOutputHeight,
                    diffusionSteps,
                    startingStep
                );
                
                if(upSampleResolutionArg != UpSampleResolution._256)
                {
                    int upSampleFactor = (int)upSampleResolution;
                    heightmap = bicubicUpSampler.BicubicUpSample(reverseDiffusionOutput, upSampleFactor);
                }
                else
                {
                    heightmap = reverseDiffusionOutput.ToReadOnlyArray();
                }

                input.Dispose();
                reverseDiffusionOutput.Dispose();
            }

            return heightmap;
        }
    }
}

#endif