﻿using System;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace SUI;

public class SButtonOptions : SUiElement<SButtonOptions>
{
    public Button ButtonObject;

    public SButtonOptions(GameObject root) : base(root)
    {
        ButtonObject = root.GetComponent<Button>();
        TextObject = root.FindGet<TextMeshProUGUI>("ContentPanel/TextBase");
        root.Destroy<LocalizeStringEvent>();

        FontSize(30);
        root.SetActive(true);
    }

    public SButtonOptions Notify(Action action)
    {
        ButtonObject.onClick.AddListener(action);
        return this;
    }
}