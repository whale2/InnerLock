using System;
using UnityEngine;
using System.Collections;
using System.Diagnostics;

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
		[KSPField (isPersistant = true)]
		public bool canLockToOtherShip = false;

		[KSPField (isPersistant = true)]
		public bool ecConstantDrain = false;

		[KSPField (isPersistant = true)]
		public float ecConsumption = 1f;

		[KSPField (isPersistant = true)]
		public string lockingTo = "lockLatch";

		[KSPField (isPersistant = true)]
		public float lockStrength = 50f;

		[KSPField (isPersistant = true)]
		public float minOffset = 0.01f;

		[KSPField (isPersistant = true)]
		public float minRoll = 0.99f;

		[KSPField (isPersistant = true)]
		public bool isSlave = false;

		[KSPField (isPersistant = true)]
		public bool isMaster = false;

		// Runtime attributes
		public FixedJoint lockJoint;

		private FXGroup lockSound;
		private FXGroup unlockSound;

		public string unlockSoundPath = "InnerLock/Sounds/unlock";
		public string lockSoundPath = "InnerLock/Sounds/lock";

		// Runtime flags and vars
		public bool isPrimed = false;
		private bool latchSlipped = false;
		private bool lockStarted = false;
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
			}
		}

		public void OnDestroy ()
		{
			GameEvents.onVesselGoOffRails.Remove (offRails);
			GameEvents.onVesselGoOnRails.Remove (onRails);
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

		[KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "engageLock")]
		public void engageLock ()
		{
			isPrimed = true;
			setEmissionColor (Color.red);
			Events ["engageLock"].active = false;
			Events ["disengageLock"].active = true;
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
			Events ["engageLock"].active = !isLocked;
			Events ["disengageLock"].active = isLocked;
			Part otherLockPart = null;
			if (isLocked && lockJoint == null && pairLockPartId != 0) {
				otherLockPart = FlightGlobals.FindPartByID (pairLockPartId);
				if (part != null)
					lockHasp (part, true);
			}
			if (otherLockPart != null && (isMaster || isSlave)) {
				if (otherLockPart.Modules.Contains ("LockMechanism")) {
					otherLock = (LockMechanism)otherLockPart.Modules ["LockMechanism"];
				} else {
					printDebug("can't find LockMechanism module in part id " + pairLockPartId);
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
			float dotfwd = Math.Abs (Vector3.Dot (otherPart.transform.forward, transform.forward));
			float offset = Vector3.Distance (
				Vector3.ProjectOnPlane (transform.position, transform.up), 
				Vector3.ProjectOnPlane (otherPart.transform.position, transform.up));

			if (-dotup < minRoll || dotfwd < minRoll || offset > minOffset) {

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
			printDebug ("lockHasp; part = " + latch.name);
			// If we're not restoring the state after timewarp/load, perform
			// what it takes to lock the latch
			if (!isRelock) {
				float num = part.RequestResource ("ElectricCharge", this.ecConsumption);
				if (num < this.ecConsumption) {
					ScreenMessages.PostScreenMessage ("Not enough electric charge to lock the hasp!");
					return;
				}
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
			Events ["engageLock"].active = false;
		}

		// Locking takes some time to complete. Set up joint after sound has done playing
		public IEnumerator finalizeLock (Part latch, bool isRelock)
		{
			printDebug ("finalize lock");
			pairLockPartId = latch.flightID;
			if (!isRelock)
				yield return new WaitForSeconds (lockSound.audio.clip.length);

			if (latchSlipped) {
				printDebug ("latch slipped");
				pairLockPartId = 0;
				ScreenMessages.PostScreenMessage ("Latch slipped! Can't lock");
				yield break;
			}

			if (!isPrimed) {
				// Disengaged during the lock
				pairLockPartId = 0;
				yield break;
			}

			if (!isSlave) {
				printDebug ("creating joint");
				lockJoint = part.gameObject.AddComponent<FixedJoint> ();
				lockJoint.connectedBody = latch.rb;
				lockJoint.breakForce = lockStrength;
				lockJoint.breakTorque = lockStrength;
				printDebug ("locked");
				if (!isRelock)
					ScreenMessages.PostScreenMessage ("Latch locked");

				// We're our own master for this call
				finalizeLockByMasterRequest ();
			}
				
			if (isMaster) {
				otherLock.finalizeLockByMasterRequest ();
			}
			setEmissionColor (Color.green);
		}

		// Master signalled lock completion
		public void finalizeLockByMasterRequest() {

			lockStarted = false;
			isLocked = true;
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
				if (!isSlave) {
					// Not playing two sounds at once
					unlockSound.audio.Play ();
				}
				StartCoroutine (finalizeUnlock ());
			} else {
				setEmissionColor (Color.black);
				Events ["engageLock"].active = true;
			}
		}

		private IEnumerator finalizeUnlock ()
		{
			printDebug ("finalize unlock; master: " + isMaster + "; slave: " + isSlave);
			yield return new WaitForSeconds (unlockSound.audio.clip.length);

			printDebug ("destroying joint");
			if (lockJoint != null) {
				Destroy (lockJoint);
			}
			lockJoint = null;
			isLocked = false;
			isPrimed = false;
			isMaster = false;
			isSlave = false;
			pairLockPartId = 0;
			setEmissionColor (Color.black);
			Events ["engageLock"].active = true;
			printDebug ("LockMechanism: unlocked");
		}

		private void setEmissionColor(Color color) {

			printDebug ("transform: " + part.transform.name);
			printDebug ("body: " + gameObject.transform.Find ("Body"));
			printDebug ("body: " + gameObject.transform.FindChild ("Body"));

			Renderer [] renderers = gameObject.GetComponentsInChildren<Renderer> ();
		
			foreach (Renderer renderer in renderers) {
				if (renderer != null) {
					foreach (Material mat in renderer.materials) {
						printDebug ("renderer=" + renderer + "; material=" + mat);
						mat.SetColor ("_EmissionColor", color);
						mat.SetColor ("_Color", color);
					}
				}
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

