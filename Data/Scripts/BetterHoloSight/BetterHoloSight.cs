using System;
using System.Collections.Generic;
using System.IO;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Weapons;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum; // HACK bypass non-whitelisted MyBillboard to get to the whitelisted BlendTypeEnum

namespace Digi.BetterHoloSight
{
    // This class is be used to simulate a holographic sight.
    // Usage:
    //   Rifle model needs to have a dummy named holosight_rectangle or holosight_circle depending on what shape it needs to limit within.
    //   Its shape should be either volumetric box or sphere to help visualize it better.
    //   The position and size of it will determine the window which the reticle can be seen through, image reference: https://i.imgur.com/4NS5w2K.png
    //   Dummy's center, width and height are used for rectangle type and its center and width are used for circle type.
    //   After that, this script needs to be edited in SetupGuns() to add the rifles you want to affect.

    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class BetterHoloSightMod : MySessionComponentBase
    {
        private void SetupGuns()
        {
            // physical item's subtypeId
            AddGun("PreciseAutomaticRifleItem", new DrawSettings()
            {
                // uncomment/add things you wanna change only for this gun, otherwise it'll use the defaults from DrawSettings.

                //ReticleMaterial = MyStringId.GetOrCompute("BetterHoloSight_Reticle"),
                //ReticleColor = new Color(255, 0, 0).ToVector4() * 2,
                //ReticleSize = 0.008f,
                //FadeStartRatio = 0.5,
                ReplaceModel = @"Models\Weapons\PrecisionRifle.mwm",
            });

            AddGun("UltimateAutomaticRifleItem", new DrawSettings()
            {
                ReticleColor = new Color(0, 255, 0).ToVector4() * 1,
                ReticleSize = 0.01f,
                ReplaceModel = @"Models\Weapons\UltimateRifle.mwm",
            });
        }

        private class DrawSettings
        {
            // editing these affects defaults
            public MyStringId ReticleMaterial = MyStringId.GetOrCompute("BetterHoloSight_Reticle"); // material from TransparentMaterials SBC.
            public Vector4 ReticleColor = new Color(255, 0, 0).ToVector4() * 2; // color R,G,B; the multiplication at the end increases intensity
            public float ReticleSize = 0.008f; // size in meters, needs to be pretty tiny.
            public double FadeStartRatio = 0.8; // angle % at which it starts to fade. 0 means it starts fading as soon as it's not centered; 1 means effectively no fading.
            public string ReplaceModel = null; // replace the rifle's model with a model from the current mod, useful for modifying vanilla rifles or other mods' without redefining their definition.

            // caching stuff, not for editing
            internal bool Processed = false;
            internal SightType Type;
            internal Matrix DummyMatrix;
            internal double MaxAngleH;
            internal double MaxAngleV;
        }

        // not for editing below this point

        private const float MAX_VIEW_DIST_SQ = 5 * 5;
        private const double RETICLE_FRONT_OFFSET = 0.25;
        private const double PROJECTED_DISTANCE = 400; // if this is too large it will cause errors on the angle calculations
        private const BlendTypeEnum RETICLE_BLEND_TYPE = BlendTypeEnum.SDR;

        private const string DUMMY_PREFIX = "holosight";
        private const string DUMMY_RECTANGLE_SUFFIX = "_rectangle";
        private const string DUMMY_CIRCLE_SUFFIX = "_circle";

        private List<DrawData> drawInfo;
        private Dictionary<string, IMyModelDummy> dummies;
        private Dictionary<MyDefinitionId, DrawSettings> drawSettings;

        private enum SightType
        {
            Unknown = 0,
            Rectangle,
            Circle,
        }

        private class DrawData
        {
            public readonly IMyEntity Entity;
            public readonly DrawSettings Settings;

            public DrawData(IMyEntity ent, DrawSettings settings)
            {
                Entity = ent;
                Settings = settings;
            }
        }

        public override void LoadData()
        {
            Log.ModName = "Better Holo Sight";

            if(MyAPIGateway.Utilities.IsDedicated)
                return;

            drawSettings = new Dictionary<MyDefinitionId, DrawSettings>(MyDefinitionId.Comparer);
            drawInfo = new List<DrawData>();
            dummies = new Dictionary<string, IMyModelDummy>();

            SetupGuns();
            ReplaceModels();

            MyAPIGateway.Entities.OnEntityAdd += EntityAdded;
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Entities.OnEntityAdd -= EntityAdded;
        }

        private void AddGun(string subType, DrawSettings settings)
        {
            drawSettings.Add(new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), subType), settings);
        }

        private void ReplaceModels()
        {
            foreach(var kv in drawSettings)
            {
                if(kv.Value.ReplaceModel == null)
                    continue;

                MyPhysicalItemDefinition def;

                if(MyDefinitionManager.Static.TryGetPhysicalItemDefinition(kv.Key, out def))
                {
                    def.Model = Path.Combine(ModContext.ModPath, kv.Value.ReplaceModel);
                }
            }
        }

        private void EntityAdded(IMyEntity ent)
        {
            try
            {
                var floatingObject = ent as MyFloatingObject;

                if(floatingObject != null)
                {
                    AddSupportedGun(ent, floatingObject.ItemDefinition.Id);
                    return;
                }

                var handHeldItem = ent as IMyAutomaticRifleGun;

                if(handHeldItem != null)
                {
                    AddSupportedGun(ent, handHeldItem.PhysicalItemId);
                    return;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void AddSupportedGun(IMyEntity ent, MyDefinitionId physItemId)
        {
            DrawSettings settings;

            if(!drawSettings.TryGetValue(physItemId, out settings))
                return;

            for(int i = 0; i < drawInfo.Count; ++i)
            {
                if(drawInfo[i].Entity == ent)
                    return;
            }

            if(!settings.Processed) // parse dummies only once per gun subtype
            {
                settings.Processed = true;

                dummies.Clear();
                ent.Model.GetDummies(dummies);

                foreach(var dummy in dummies.Values)
                {
                    if(dummy.Name.StartsWith(DUMMY_PREFIX))
                    {
                        var dummyMatrix = dummy.Matrix;
                        var gunMatrix = ent.WorldMatrix;

                        var reticleProjectedPosition = Vector3D.Transform(dummyMatrix.Translation, gunMatrix) + gunMatrix.Forward * PROJECTED_DISTANCE;
                        var sightPositionLocal = dummyMatrix.Translation;

                        if(dummy.Name.EndsWith(DUMMY_RECTANGLE_SUFFIX))
                        {
                            settings.Type = SightType.Rectangle;

                            var edgePosH = Vector3D.Transform(sightPositionLocal + dummyMatrix.Left * 0.5f, gunMatrix);
                            var reticleToEdgePosH = Vector3D.Normalize(reticleProjectedPosition - edgePosH);
                            settings.MaxAngleH = Math.Acos(Vector3D.Dot(gunMatrix.Forward, reticleToEdgePosH));

                            var edgePosV = Vector3D.Transform(sightPositionLocal + dummyMatrix.Up * 0.5f, gunMatrix);
                            var reticleToEdgePosV = Vector3D.Normalize(reticleProjectedPosition - edgePosV);
                            settings.MaxAngleV = Math.Acos(Vector3D.Dot(gunMatrix.Forward, reticleToEdgePosV));
                        }
                        else if(dummy.Name.EndsWith(DUMMY_CIRCLE_SUFFIX))
                        {
                            settings.Type = SightType.Circle;

                            var edgePos = Vector3D.Transform(sightPositionLocal + dummyMatrix.Left * 0.5f, gunMatrix);
                            var reticleToEdgePos = Vector3D.Normalize(reticleProjectedPosition - edgePos);
                            settings.MaxAngleH = Math.Acos(Vector3D.Dot(gunMatrix.Forward, reticleToEdgePos));
                        }
                        else
                        {
                            Log.Error($"{physItemId.SubtypeName} has unsupported dummy suffix: {dummy.Name}", Log.PRINT_MSG);
                            return;
                        }

                        settings.DummyMatrix = dummy.Matrix;
                        break;
                    }
                }

                dummies.Clear();
            }

            drawInfo.Add(new DrawData(ent, settings));
        }

        public override void Draw()
        {
            try
            {
                int count = drawInfo.Count;

                if(count == 0)
                    return;

                var camMatrix = MyAPIGateway.Session.Camera.WorldMatrix;

                for(int i = count - 1; i >= 0; i--)
                {
                    var data = drawInfo[i];
                    var ent = data.Entity;
                    var settings = data.Settings;

                    if(ent.MarkedForClose)
                    {
                        drawInfo.RemoveAtFast(i);
                        continue;
                    }

                    var gunMatrix = ent.WorldMatrix;

                    var fwDot = gunMatrix.Forward.Dot(camMatrix.Forward);
                    if(fwDot <= 0)
                        continue; // looking more than 90deg away from the direction of the gun.

                    if(Vector3D.DistanceSquared(camMatrix.Translation, gunMatrix.Translation) > MAX_VIEW_DIST_SQ)
                        continue; // too far away to be seen

                    var dummyMatrix = settings.DummyMatrix; // scaled exactly like the dummy from the model

                    var reticleProjectedPosition = Vector3D.Transform(dummyMatrix.Translation, gunMatrix) + gunMatrix.Forward * PROJECTED_DISTANCE;

                    //if(IsLocalPlayerAimingThisGun(data.Entity))
                    //{
                    //    var centerScreenPos = camMatrix.Translation + camMatrix.Forward * Vector3D.Distance(camMatrix.Translation, reticlePosition);
                    //    MyTransparentGeometry.AddBillboardOriented(data.Settings.ReticleMaterial, data.Settings.ReticleColor, centerScreenPos, gunMatrix.Left, gunMatrix.Up, data.Settings.ReticleSize, blendType: RETICLE_BLEND_TYPE);
                    //    continue;
                    //}

                    var sightPosition = Vector3D.Transform(dummyMatrix.Translation, gunMatrix);

                    var fwOffsetDot = gunMatrix.Forward.Dot(sightPosition - camMatrix.Translation);
                    if(fwOffsetDot < 0)
                        continue; // camera is ahead of sight, don't draw reticle

                    if(settings.Type == SightType.Rectangle)
                    {
                        var camToReticleDir = Vector3D.Normalize(reticleProjectedPosition - camMatrix.Translation);
                        double angleH = Math.Acos(Vector3D.Dot(gunMatrix.Left, camToReticleDir)) - (Math.PI / 2); // subtracting 90deg
                        double angleV = Math.Acos(Vector3D.Dot(gunMatrix.Up, camToReticleDir)) - (Math.PI / 2);

                        // simplifies math later on
                        angleH = Math.Abs(angleH);
                        angleV = Math.Abs(angleV);

                        if(angleH < settings.MaxAngleH && angleV < settings.MaxAngleV)
                        {
                            var alphaH = GetAlphaForAngle(settings.FadeStartRatio, angleH, settings.MaxAngleH);
                            var alphaV = GetAlphaForAngle(settings.FadeStartRatio, angleV, settings.MaxAngleV);

                            var camToSightDistance = Vector3D.Distance(sightPosition, camMatrix.Translation) + RETICLE_FRONT_OFFSET;
                            var reticlePosition = camMatrix.Translation + (camToReticleDir * camToSightDistance);

                            MyTransparentGeometry.AddBillboardOriented(settings.ReticleMaterial, settings.ReticleColor * (alphaH * alphaV), reticlePosition, gunMatrix.Left, gunMatrix.Up, settings.ReticleSize, blendType: RETICLE_BLEND_TYPE);
                        }
                    }
                    else if(settings.Type == SightType.Circle)
                    {
                        var camToReticleDir = Vector3D.Normalize(reticleProjectedPosition - camMatrix.Translation);
                        double angle = Math.Acos(Vector3D.Dot(gunMatrix.Forward, camToReticleDir));

                        if(angle < settings.MaxAngleH)
                        {
                            var alpha = GetAlphaForAngle(settings.FadeStartRatio, angle, settings.MaxAngleH);

                            var camToSightDistance = Vector3D.Distance(sightPosition, camMatrix.Translation) + RETICLE_FRONT_OFFSET;
                            var reticlePosition = camMatrix.Translation + (camToReticleDir * camToSightDistance);

                            MyTransparentGeometry.AddBillboardOriented(settings.ReticleMaterial, settings.ReticleColor * alpha, reticlePosition, gunMatrix.Left, gunMatrix.Up, settings.ReticleSize, blendType: RETICLE_BLEND_TYPE);
                        }
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private float GetAlphaForAngle(double fadeRatio, double absAngle, double boundaryAngle)
        {
            var fadeOutStartAngle = (boundaryAngle * fadeRatio);

            if(absAngle > fadeOutStartAngle)
            {
                var amount = (absAngle - fadeOutStartAngle) / (boundaryAngle - fadeOutStartAngle);
                return 1f - (float)amount;
            }

            return 1f;
        }

        //private bool IsLocalPlayerAimingThisGun(IMyEntity ent)
        //{
        //    var rifle = ent as IMyAutomaticRifleGun;
        //
        //    if(rifle != null)
        //    {
        //        var character = MyAPIGateway.Session?.Player?.Character;
        //        var weaponComp = character?.Components?.Get<MyCharacterWeaponPositionComponent>();
        //
        //        if(weaponComp != null && rifle.Owner == character && MyAPIGateway.Session.CameraController == character)
        //            return weaponComp.IsInIronSight;
        //    }
        //
        //    return false;
        //}
    }
}