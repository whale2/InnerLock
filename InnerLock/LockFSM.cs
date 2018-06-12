using System;
using System.Collections.Generic;

namespace InnerLock
{
    public class LockFSM
    {
        // Lock Finite State Machine   

        public enum State
        {
            Idle,
            Ready,
            Locking,
            Locked,
            Unlocking,
            Broken
        }
        
        public enum Event
        {
            Engage,
            Contact,
            Slip,
            Lock,
            Disengage,
            Unlock,
            Release,
            Break
        }

        private Action[,] fsm;
        
        public State state;

        public Dictionary<String, Action> actionDelegates;

        public LockFSM() : this(State.Idle) {}

        public LockFSM(State initialState)
        {
            state = initialState;
            actionDelegates = new Dictionary<string, Action>();
            fsm = new Action[,]
            {
                // All Events and States of FSM and possible transitions
                // Engage,  Contact, Slip,  Lock,    Disengage,  Unlock,    Release   Break
                {  Engage,  null,    null,  null,    null,       null,      null,      Break }, // Idle
                {  null,    Lock,    null,  null,    Disengage,  null,      null,      Break }, // Ready
                {  null,    null,    Slip,  Locked,  Disengage,  null,      null,      Break }, // Locking
                {  null,    null,    null,  null,    null,       Unlock,    null,      Break }, // Locked
                {  null,    null,    null,  null,    null,       null,      Unlocked,  Break }, // Unlocking
                {  null,    null,    null,  null,    null,       null,      null,      null  }  // Broken
            };
        }
        
        public void processEvent(Event e)
        {
            var action = fsm[(int) this.state, (int) e];
            if (action != null)
            {
                action.Invoke();
            }
        }

        private void act(String actionName)
        {
            Action action;
            if (actionDelegates.TryGetValue(actionName, out action))
            {
                action.Invoke();
            }
        }

        public void Engage()
        {
            state = State.Ready;
            act("Engage");
        }

        public void Lock()
        {
            state = State.Locking;
            act("Lock");
        }

        public void Slip()
        {
            state = State.Ready;
            act("Slip");
        }

        public void Locked()
        {
            state = State.Locked;
            act("Locked");
        }

        public void Disengage()
        {
            state = State.Idle;

            if (state == State.Locking)
            {
                act("CancelLocking");
            }
            
            act("Disengage");
        }

        public void Unlock()
        {
            state = State.Unlocking;
            act("Unlock");
        }

        public void Unlocked()
        {
            state = State.Idle;
            act("Unlocked");
        }

        public void Break()
        {
            state = State.Broken;
            act("Break");
        }
        
    }
}