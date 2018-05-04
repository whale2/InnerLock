using System;
using UnityEngine;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;

namespace InnerLock
{
	public class LockMechanism : PartModule
	{

		// Runtime parameters
		[KSPField (isPersistant = true)]
		public bool isLocked = false;

		[KSPField (isPersistant = true)]
		public uint pairLockPartId = 0;

		// Config parameters
		[KSPField]
		public bool canLockToOtherShip = false;

		[KSPField]
		public bool ecConstantDrain = false;

		[KSPField]
		public float ecConsumption = 1f;

		[KSPField]
		public string lockingTo = "lockLatch";

		[KSPField]
		public float lockStrength = 50f;

		[KSPField]
		public float maxOffset = 0.01f;

		[KSPField]
		public float maxRollDeviation = 0.01f;

		[KSPField]
		public string allowedRolls = "0";

		[KSPField (isPersistant = true)]
		public bool isSlave = false;

		[KSPField (isPersistant = true)]
		public bool isMaster = false;

		public float[] configuredRolls = { 0.0f };

		// Runtime attributes
		public ConfigurableJoint lockJoint;
		public AttachNode attachNode;
		public PartJoint partJoint;

		private FXGroup lockSound;
		private FXGroup unlockSound;

		public string unlockSoundPath = "InnerLock/Sounds/unlock";
		public string lockSoundPath = "InnerLock/Sounds/lock";

		// Runtime flags and vars
		public bool isPrimed = false;
		private bool latchSlipped = false;
		private bool lockStarted = false;
		private bool unlockStarted = false;
		private bool msgPosted = false;
		private LockMechanism otherLock;
		
		public LockMechanism ()
		{
		}

		public override void OnStart (StartState state)
		{
			base.OnStart (state);
			if (state != StartState.Editor) {
				GameEvents.onVesselGoOffRails.Add (offRails);
				GameEvents.onVesselGoOnRails.Add (onRails);
				GameEvents.onPartJointBreak.Add (partJointBreak);
				GameEvents.onJointBreak.Add (jointBreak);
				GameEvents.onPartDie.Add (partDie);
			}

			List<float> rolls = new List<float> ();
			foreach (string roll in allowedRolls.Split (',')) {
				rolls.Add (float.Parse (roll.Trim ()));
			}
			printDebug ("allowed rolls: " + allowedRolls);
		}

		public void OnDestroy ()
		{
			GameEvents.onVesselGoOffRails.Remove (offRails);
			GameEvents.onVesselGoOnRails.Remove (onRails);
			GameEvents.onPartJointBreak.Remove (partJointBreak);
			GameEvents.onJointBreak.Remove (jointBreak);
			GameEvents.onPartDie.Remove (partDie);
		}
			
		[KSPEvent (guiName = "Disengage Lock", guiActive = true, guiActiveEditor = false, name = "disengageLock")]
		public void disengageLock ()
		{
			unlockHasp ();
			if (isMaster || isSlave) {
				// There's a pair lock on the other end. Tell them to unlock;
				otherLock.unlockHasp ();
			}
		}

		[KSPAction ("Disengage Lock", actionGroup = KSPActionGroup.None)]
		public void actionDisenage(KSPActionParam param)
		{
			disengageLock();
		}

		[KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "engageLock")]
		public void engageLock ()
		{
			isPrimed = true;
			setEmissiveColor (Color.yellow);
			Events ["engageLock"].active = false;
			Events ["disengageLock"].active = true;
		}

		[KSPAction("Engage Lock", actionGroup = KSPActionGroup.None)]
		public void actionEngage(KSPActionParam param)
		{
			engageLock ();
		}

		[KSPAction("Toggle Lock", actionGroup = KSPActionGroup.None)]
		public void actionToggle(KSPActionParam param)
		{
			if (lockStarted) {
				return;
			}
			if (isPrimed || isLocked) {
				disengageLock ();
			} else {
				engageLock ();
			}
		}

		public void onRails (Vessel v)
		{
			// ???
			if (v != vessel)
				return;
			base.Events ["engageLock"].active = false;
			base.Events ["disengageLock"].active = false;
		}

		public void offRails (Vessel v)
		{
			// ???
			if (v != vessel)
				return;

			if (isLocked) {
				setEmissiveColor (Color.green);
			}
			else {
				setEmissiveColor (Color.black);
			}
			Events ["engageLock"].active = !isLocked;
			Events ["disengageLock"].active = isLocked;
			Part otherLockPart = null;
			if (isLocked && lockJoint == null && pairLockPartId != 0) {
				// If locked and there's no joint, we're restoring from save and joint
				// must be re-created
				printDebug ("restoring joint; pair=" + pairLockPartId + "; isMaster=" + isMaster);
				otherLockPart = FlightGlobals.FindPartByID (pairLockPartId);
				if (otherLockPart != null) {
					// In case of genderless locking, get other part locking module
					if ((isMaster || isSlave) && otherLockPart.Modules.Contains ("LockMechanism")) {
						otherLock = (LockMechanism)otherLockPart.Modules ["LockMechanism"];
					}
					lockHasp (otherLockPart, true);
				}
			}

			isPrimed = false;
			latchSlipped = false;
			lockStarted = false;
			msgPosted = false;
		}

		public void OnCollisionEnter (Collision c)
		{
			// Lock halves start touching each other. Activate locking mechanism
			if (isPrimed && !isLocked && !lockStarted) {
				this.lockToLatch (c);
			}
		}	

		public void OnCollisionExit (Collision c)
		{
			// No more contact. Abort locking operation.
			msgPosted = false;
			latchSlipped = true;
			lockStarted = false;
		}

		public void OnCollisionStay (Collision c)
		{
			// Lock halves continue touching each other. Activate locking mechanism if not already done
			if (isPrimed && !isLocked && !lockStarted)
				this.lockToLatch (c);
		}

		public void lockToLatch (Collision c)
		{
			foreach(ContactPoint cp in c.contacts) {

				Part p = cp.otherCollider.attachedRigidbody.GetComponent<Part> ();

				// Check if other part is suitable
				if (!checkOtherHalf (p))
					continue;

				// Check if it is properly aligned 
				if (!checkRollAndDot (p))
					continue;

				latchSlipped = false;
				lockStarted = true;
				lockHasp (p, false);
				break;
			}
		}

		private bool checkOtherHalf(Part otherPart) {

			if (otherPart == null) {
				return false;
			}

			if (!otherPart.name.Replace('.','_').Equals (lockingTo.Replace('.','_'))) {
				return false;
			}

			if (!canLockToOtherShip && otherPart.vessel != vessel) {
				printDebug("canLockToOtherShip = " + canLockToOtherShip + 
					"; other vessel = " + otherPart.vessel.name);
				return false;
			}
			return true;
		}

		private bool checkRollAndDot(Part otherPart) {

			float dotup = Vector3.Dot (otherPart.transform.up, transform.up);
			float dotfwd = Vector3.Dot (otherPart.transform.forward, transform.forward);
			float offset = Vector3.Distance (
				Vector3.ProjectOnPlane (transform.position, transform.up), 
				Vector3.ProjectOnPlane (otherPart.transform.position, transform.up));

			bool aligned = true;
			if (-dotup < maxRollDeviation || offset > maxOffset) {
				aligned = false;
			}

			foreach (float roll in allowedRolls) {
				if (Math.Abs (dotfwd - roll) < maxRollDeviation) {
					aligned = true;
					break;
				}
			}

			if (!aligned) {
				if (!msgPosted) {
					printDebug ("dotup = " + dotup + "; dotfwd = " + dotfwd + "; offset = " + offset);
					ScreenMessages.PostScreenMessage ("Latch not aligned - can't lock");
					msgPosted = true;
				}
				return false;
			}
			return true;
		}

		public void lockHasp (Part latch, bool isRelock)
		{
			printDebug ("lockHasp; part = " + latch.name + "; id = " + latch.flightID + "; relock = " + isRelock);
			// If we're not restoring the state after timewarp/load, perform
			// what it takes to lock the latch
			if (!isRelock) {
				float num = part.RequestResource ("ElectricCharge", this.ecConsumption);
				if (num < this.ecConsumption) {
					ScreenMessages.PostScreenMessage ("Not enough electric charge to lock the hasp!");
					return;
				}
				setEmissiveColor (Color.cyan);
				isSlave = false;
				// If we use genderless locking, tell the other part that we are leading
				if (latch.name == part.name && latch.Modules.Contains ("LockMechanism")) {
					// Both locks could be primed. In that case assing master status
					// to the part with lesser flightID
					otherLock = (LockMechanism)latch.Modules ["LockMechanism"];
					if (!otherLock.isPrimed || part.flightID < latch.flightID) {
						printDebug ("acquiring master status");
						otherLock.setSlaveLock (part.flightID);
						isMaster = true;
					} else {
						printDebug ("submitting to slave status");
						isSlave = true;
					}
				}
					
				if (!isSlave) {
					if (lockSound == null)
						lockSound = this.createAudio (base.part.gameObject, this.lockSoundPath);
					this.lockSound.audio.Play ();
				}
			}
			StartCoroutine (this.finalizeLock (latch, isRelock));
		}

		// Signalled by master about locking. Set flags
		public void setSlaveLock(uint masterPartId) {
			isSlave = true;
			isPrimed = false;
			isMaster = false;
			pairLockPartId = masterPartId;
			Part otherLockPart = FlightGlobals.FindPartByID (masterPartId);
			otherLock = (LockMechanism)otherLockPart.Modules ["LockMechanism"];
			setEmissiveColor (Color.cyan);
			Events ["engageLock"].active = false;
		}

		// Locking takes some time to complete. Set up joint after sound has done playing
		public IEnumerator finalizeLock (Part latch, bool isRelock)
		{
			printDebug ("finalize lock; other part=" + latch.flightID);
			pairLockPartId = latch.flightID;
			if (!isRelock)
				yield return new WaitForSeconds (lockSound.audio.clip.length);

			if (!isRelock && latchSlipped) {
				printDebug ("latch slipped");
				pairLockPartId = 0;
				ScreenMessages.PostScreenMessage ("Latch slipped! Can't lock");
				yield break;
			}

			if (!isPrimed && !isLocked) {
				// Disengaged during the lock
				printDebug("not primed and not locked");
				pairLockPartId = 0;
				yield break;
			}

			if (!isSlave) {
				printDebug ("creating joint");
				lockJoint = part.gameObject.AddComponent<ConfigurableJoint> ();
				lockJoint.connectedBody = latch.rb;
				lockJoint.breakForce = lockStrength;
				lockJoint.breakTorque = lockStrength;
				lockJoint.xMotion = ConfigurableJointMotion.Locked;
				lockJoint.yMotion = ConfigurableJointMotion.Locked;
				lockJoint.zMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularXMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularYMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularZMotion = ConfigurableJointMotion.Locked;
				lockJoint.linearLimit = new SoftJointLimit { bounciness = 0.9f, contactDistance = 0, limit = 0.01f };
				lockJoint.linearLimitSpring = new SoftJointLimitSpring { damper = 10000, spring = 0 };
				lockJoint.projectionMode = JointProjectionMode.PositionAndRotation;
				lockJoint.projectionDistance = 0;
				lockJoint.projectionAngle = 0;
				lockJoint.targetPosition = latch.transform.position;
				lockJoint.anchor = latch.transform.position;

				printDebug ("creating attachNode");

				Vector3 normDir = (part.transform.position - latch.transform.position).normalized;
				

				attachNode = new AttachNode {id = Guid.NewGuid().ToString(), attachedPart = latch};
				attachNode.breakingForce = lockStrength;
				attachNode.breakingTorque = lockStrength;
				attachNode.position = latch.partTransform.InverseTransformPoint(latch.partTransform.position);
				attachNode.orientation = latch.partTransform.InverseTransformDirection(normDir);
				attachNode.size = 1;
				attachNode.ResourceXFeed = false;
				attachNode.attachMethod = AttachNodeMethod.FIXED_JOINT;
				part.attachNodes.Add(attachNode);
				attachNode.owner = part;
				partJoint = PartJoint.Create(part, latch, attachNode, null, AttachModes.SRF_ATTACH);

				printDebug ("locked");
				if (!isRelock)
					ScreenMessages.PostScreenMessage ("Latch locked");

				// We're our own master for this call
				finalizeLockByMasterRequest ();
			}
				
			if (isMaster) {
				printDebug ("master; otherLock id = " + otherLock.part.flightID);
				otherLock.finalizeLockByMasterRequest ();
			}
			setEmissiveColor (Color.green);
		}

		public FixedJoint createJoint(Part target) {
			FixedJoint joint = part.gameObject.AddComponent<FixedJoint> ();
			joint.connectedBody = target.rb;
			joint.breakForce = lockStrength;
			joint.breakTorque = lockStrength;
			return joint;
		}

		// Master signalled lock completion
		public void finalizeLockByMasterRequest() {

			lockStarted = false;
			isLocked = true;
			setEmissiveColor (Color.green);
			Events ["disengageLock"].active = true;
		}

		// Unlocking takes time as well
		public void unlockHasp ()
		{
			Events ["disengageLock"].active = false;
			isPrimed = false;
			if (unlockSound == null)
				unlockSound = createAudio (part.gameObject, unlockSoundPath);
			
			if (isLocked) {
				setEmissiveColor (Color.cyan);
				if (!isSlave) {
					// Not playing two sounds at once
					unlockSound.audio.Play ();
				}
				StartCoroutine (finalizeUnlock (false));
			} else {
				setEmissiveColor (Color.black);
				Events ["engageLock"].active = true;
			}
		}
			
		private IEnumerator finalizeUnlock (bool broken)
		{
			printDebug ("finalize unlock; master: " + isMaster + "; slave: " + isSlave + "; broken: " + broken);
			unlockStarted = true;
			if (!broken) {
				yield return new WaitForSeconds (unlockSound.audio.clip.length);
			}

			if ((isSlave || isMaster) && !otherLock.unlockStarted) {
				StartCoroutine (otherLock.finalizeUnlock (true));
			}
			if (lockJoint != null) {
				printDebug ("destroying joint");
				DestroyImmediate (lockJoint);
				partJoint.DestroyJoint();
				if (attachNode != null) {
					part.attachNodes.Remove (attachNode);
				}
				attachNode.owner = null;
			}

			lockJoint = null;
			attachNode = null;
			isLocked = false;
			isPrimed = false;
			isMaster = false;
			isSlave = false;
			pairLockPartId = 0;
			setEmissiveColor (Color.black);
			Events ["disengageLock"].active = false;
			Events ["engageLock"].active = true;
			unlockStarted = false;
			printDebug ("unlocked");
		}

		public void partJointBreak(PartJoint joint, float breakForce) {

			if (joint.Parent != part) {
				return;
			}
			if (joint == lockJoint) {
				// disconnected from other lock. It's allright
				return;
			}
			// Seems like we just got separated from the vessel.
			// Shut down actions taking into account that we might be still
			// connected to other lock part
			printDebug ("part joint broken");
			Events ["engageLock"].active = false;
			Events ["disengageLock"].active = false;
			setEmissiveColor (Color.black);
		}

		public void jointBreak(EventReport report) {
			if (!isSlave) {
				StartCoroutine (WaitAndCheckJoint ());
			}
		}

		public void partDie(Part p) {
			if (p != part) {
				return;
			}
			if (isLocked && (isSlave || isMaster)) {
				StartCoroutine (finalizeUnlock (true));
			}
		}

		private IEnumerator WaitAndCheckJoint() {

			yield return new WaitForFixedUpdate();
			if (partJoint == null) {
				printDebug ("joint broken");
				StartCoroutine (finalizeUnlock(true));
			}
		}

		private void setEmissiveColor(Color color) {
			
			Renderer renderer = gameObject.GetComponentInChildren<Renderer> ();
		
			if (renderer != null) {
				renderer.material.SetColor ("_EmissiveColor", color);
			}
		}

		private FXGroup createAudio (GameObject obj, string audioPath)
		{
			if (obj == null)
				return null;

			FXGroup fXGroup = new FXGroup ("lockHasp");
			fXGroup.audio = obj.AddComponent<AudioSource> ();
			fXGroup.audio.volume = GameSettings.SHIP_VOLUME;
			fXGroup.audio.rolloffMode = AudioRolloffMode.Logarithmic;
			fXGroup.audio.dopplerLevel = 0;
			fXGroup.audio.maxDistance = 30;
			fXGroup.audio.loop = false;
			fXGroup.audio.playOnAwake = false;

			if (GameDatabase.Instance.ExistsAudioClip (audioPath)) {
				fXGroup.audio.clip = GameDatabase.Instance.GetAudioClip (audioPath);
			}
			else {
				printDebug ("No clip found with path " + audioPath);
			}
			return fXGroup;
		}

		internal void printDebug(String message) {

			StackTrace trace = new StackTrace ();
			String caller = trace.GetFrame(1).GetMethod ().Name;
			int line = trace.GetFrame (1).GetFileLineNumber ();
			print ("LockMechanism: " + caller + ":" + line + ": (part id=" + part.flightID + "): " + message);
		}

	}
}

