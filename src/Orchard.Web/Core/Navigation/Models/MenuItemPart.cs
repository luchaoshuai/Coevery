﻿using Orchard.ContentManagement;

namespace Orchard.Core.Navigation.Models {
    public class MenuItemPart : ContentPart<MenuItemPartRecord> {
        
        public string Url
        {
            get { return Record.Url; }
            set { Record.Url = value; }
        }

        public  string FeatureId
        {
            get { return Record.FeatureId; }
            set { Record.FeatureId = value; }
        }
    }
}
