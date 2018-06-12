using System.Collections.Generic;
using UnityEngine;

namespace InnerLock
{
    public class LockMechanism2 : PartModule
    {
        // Runtime parameters
        [KSPField (isPersistant = true)]
        public uint lockFSMState = 0;

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

        private LockFSM lockFSM;
        private LockMechanism2 otherLock;

        private bool msgPosted = false;
        
        public LockMechanism2 ()
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
            
            lockFSM = new LockFSM((LockFSM.State)lockFSMState);
            lockFSM.actionDelegates.Add("Engage", engageLock);
            lockFSM.actionDelegates.Add("Disengage", disengageLock);
        }

        public void OnDestroy ()
        {
            GameEvents.onVesselGoOffRails.Remove (offRails);
            GameEvents.onVesselGoOnRails.Remove (onRails);
            GameEvents.onPartJointBreak.Remove (partJointBreak);
            GameEvents.onJointBreak.Remove (jointBreak);
            GameEvents.onPartDie.Remove (partDie);
        }

        // Menu event handler
        [KSPEvent(guiName = "Disengage Lock", guiActive = true, guiActiveEditor = false, name = "disengageLock")]
        public void eventDisengage()
        {
            lockFSM.processEvent(LockFSM.Event.Disengage);
        }
        
        // Action group handler
        [KSPAction ("Disengage Lock", actionGroup = KSPActionGroup.None)]
        public void actionDisenage(KSPActionParam param)
        {
            lockFSM.processEvent(LockFSM.Event.Disengage);
        }
        
        public void disengageLock ()
        {
            setEmissiveColor ();
            setMenuEvents();
        }
        
        // Menu event handler
        [KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "engageLock")]
        public void eventEngage ()
        {
            lockFSM.processEvent(LockFSM.Event.Engage);
        }

        // Action group handler
        [KSPAction("Engage Lock", actionGroup = KSPActionGroup.None)]
        public void actionEngage(KSPActionParam param)
        {
            lockFSM.processEvent(LockFSM.Event.Engage);
        }

        public void engageLock()
        {
            setEmissiveColor ();
            setMenuEvents();
        }

        // Action group handler for toggling lock on and off
        [KSPAction("Toggle Lock", actionGroup = KSPActionGroup.None)]
        public void actionToggle(KSPActionParam param)
        {
            // Send unlock event if in locked state, disengage otherwise
            // We just fire the event, FSM will sort out the proper reaction
            lockFSM.processEvent(lockFSM.state == LockFSM.State.Locked ? 
                LockFSM.Event.Unlock : LockFSM.Event.Disengage);
        }

        //  Railing and derailing stuff
        public void onRails (Vessel v)
        {
            // If we're going on rails when locking is in progress, reset to the ready
            if (lockFSM.state == LockFSM.State.Locking)
            {
                lockFSM.processEvent(LockFSM.Event.Disengage);
            }

            // If we're going on rails when unlocking is in progress, fast forward to idle 
            if (lockFSM.state == LockFSM.State.Unlocking)
            {
                lockFSM.processEvent(LockFSM.Event.Release);
            }
            // Just hide the menu buttons
            Events ["engageLock"].active = false;
            Events ["disengageLock"].active = false;
        }

        public void offRails (Vessel v)
        {
            setEmissiveColor ();
            setMenuEvents();
            
            Part otherLockPart = null;
            if (lockFSM.state == LockFSM.State.Locked && lockJoint == null && pairLockPartId != 0) {
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

            msgPosted = false;
        }

        private void setMenuEvents()
        {
            switch (lockFSM.state)
            {
                case LockFSM.State.Idle:
                    Events["engageLock"].active = true;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = false;
                    break;
                
                case LockFSM.State.Ready:
                case LockFSM.State.Locking:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = true;
                    Events["unlockLock"].active = false;
                    break;
                
                case LockFSM.State.Locked:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = true;
                    break;
                
                case LockFSM.State.Unlocking:
                case LockFSM.State.Broken:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = false;
                    break;
                
            }
        }
        
        private void setEmissiveColor()
        {
            Color color;
            if (lockFSM.state == LockFSM.State.Ready)
            {
                color = Color.yellow;
            }
            else if(lockFSM.state == LockFSM.State.Locked)
            {
                color = Color.green;
            }
            else if (lockFSM.state == LockFSM.State.Locking || lockFSM.state == LockFSM.State.Unlocking)
            {
                color = Color.cyan;
            }
            else
            {
                color = Color.black;
            }
            
            Renderer renderer = gameObject.GetComponentInChildren<Renderer> ();
		
            if (renderer != null) {
                renderer.material.SetColor ("_EmissiveColor", color);
            }
        }
    }
}