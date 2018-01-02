using System;
using UnityEngine;
using System.Collections.Generic;

namespace InnerLock
{
	public class InnerLock : PartModule
	{

		[KSPField (isPersistant = true)]
		public bool isActiveForAll = false;

		[KSPField (isPersistant = true)]
		public bool isPermaLock = false;

		[KSPField (isPersistant = true)]
		public bool isSwitchable = true;

		private EventVoid onWorldStabilizationStartEvent;
		private EventVoid onWorldStabilizedEvent;

		private int framesToEnforce = 0;
		private int ticks = 3000;
		private int totalFrames = 0;

		//private List<Vessel> vesselsToUpdate;

		private bool worldStabilizationInProgress = false;

		public InnerLock ()
		{
		}

		[KSPEvent (guiName = "Disable All Internal Collisions", guiActive = false, guiActiveEditor = true, name = "disableIntraCollisions")]
		public void disableIntraCollisions ()
		{
			isActiveForAll = false;
			Events ["enableIntraCollisions"].active = true;
			Events ["disableIntraCollisions"].active = false;
		}

		[KSPEvent (guiName = "Enable All Internal Collisions", guiActive = false, guiActiveEditor = true, name = "enableIntraCollisions")]
		public void enableIntraCollisions ()
		{
			isActiveForAll = true;
			Events ["enableIntraCollisions"].active = false;
			Events ["disableIntraCollisions"].active = true;
		}

		public override void OnStart (PartModule.StartState state)
		{
			base.OnStart (state);
			if (state == StartState.Editor && isSwitchable) {
				base.Events ["enableIntraCollisions"].active = !isActiveForAll;
				base.Events ["disableIntraCollisions"].active = isActiveForAll;
			}
			else {
				GameEvents.onVesselCreate.Add (EnqueueVessel);
				GameEvents.onNewVesselCreated.Add (EnqueueVessel);
				GameEvents.onVesselGoOffRails.Add (EnqueueVessel);
				GameEvents.onVesselWasModified.Add (EnqueueVessel);
				GameEvents.OnCollisionIgnoreUpdate.Add (EnqueueVoid);
				GameEvents.OnAnimationGroupStateChanged.Add (EnqueueOnAnimation);

				onWorldStabilizationStartEvent = GameEvents.FindEvent<EventVoid> ("onWorldStabilizationStart");
				if (onWorldStabilizationStartEvent != null) 
					onWorldStabilizationStartEvent.Add (onWorldStabilizationStart);
				
				onWorldStabilizedEvent = GameEvents.FindEvent<EventVoid> ("onWorldStabilized");
				if (onWorldStabilizedEvent != null)
					this.onWorldStabilizedEvent.Add (onWorldStabilized);
			}
		}

		private void OnDestroy ()
		{
			GameEvents.onVesselCreate.Remove (EnqueueVessel);
			GameEvents.onNewVesselCreated.Remove (EnqueueVessel);
			GameEvents.onVesselGoOffRails.Remove (EnqueueVessel);
			GameEvents.onVesselWasModified.Remove (EnqueueVessel);
			GameEvents.OnAnimationGroupStateChanged.Remove (EnqueueOnAnimation);
			GameEvents.OnCollisionIgnoreUpdate.Remove (EnqueueVoid);
		}

		public void onWorldStabilizationStart ()
		{
			//Debug.Log ("InnerLock: onWorldStabilizationStart received");
			worldStabilizationInProgress = true;
		}

		public void onWorldStabilized ()
		{
			//Debug.Log ("InnerLock: onWorldStabilized received");
			worldStabilizationInProgress = false;
		}

		public void EnqueueOnAnimation (ModuleAnimationGroup group, bool state)
		{
			EnqueueVessel (vessel);
		}

		public void EnqueueVessel (Vessel v)
		{
			totalFrames = 0;
			framesToEnforce = (int)((float)this.ticks * Time.deltaTime);
		}

		public void EnqueueVoid ()
		{
			EnqueueVessel (vessel);
		}

		public void FixedUpdate ()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel.packed)
				return;
			if (totalFrames < framesToEnforce || worldStabilizationInProgress) {
				AdjustColliders (vessel);
				totalFrames++;
			}
		}

		public void AdjustColliders (Vessel v)
		{
			gameObject.layer = 30;

			foreach (Part p in v.parts) {
				// we don't collide with ourselves
				if (p != part) {
					// we collide if:
					// isActiveForAll, or
					// other part has InnerLock module and both are set to permaLock

					bool otherIsPermaLock = false;
					if (p.Modules.Contains("InnerLock")) {
						PartModule partModule = p.Modules ["InnerLock"];
						otherIsPermaLock = partModule.Fields ["isPermaLock"].GetValue<bool> (partModule);
					}
					if (isActiveForAll || (isPermaLock & otherIsPermaLock)) {
						foreach(Collider c in part.GetComponentsInChildren<Collider>())
							foreach(Collider c2 in p.GetComponentsInChildren<Collider>())
								Physics.IgnoreCollision (c, c2, false);
					}
				}
			}
			Physics.IgnoreLayerCollision (30, 30, false);
		}

	}
}

